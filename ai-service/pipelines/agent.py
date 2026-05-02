"""LangGraph-driven chat agent.

Why a graph (vs a single Gemini call with function-calling)
===========================================================
The single-shot chat path that lived in api/chat.py before this module
hard-coded one retrieval pass and one Gemini stream. That works for
"answer this question from this report," but the moment the user asks
something that needs to compose tools — "compare reports A and B,"
"find similar reports to my saved ones," "show me energy reports from
2024 and pick the most relevant one to my interests" — the single-shot
path either fails silently or returns a vague answer.

LangGraph gives us:

  • a typed state (messages, tool-call counter, per-turn auth context)
    that flows through deterministic nodes,
  • a `ToolNode` that runs the typed Pydantic-validated tools from
    services/tools.py with bounded output sizes,
  • automatic event streaming via `astream_events(version="v2")` so the
    SSE handler in api/chat.py can fan out tool_call / tool_result /
    token events without us hand-rolling a state machine,
  • Langfuse callback wiring in one place — every Gemini call inside
    the loop is captured under one trace, including tool spans.

Architecture
============

    START → agent ──────► (last_msg has tool_calls?) ──► tools ─┐
                                       │                         │
                                       └── no, or hop_cap hit ──► END
                                                                 ▲
                                                                 │
                                                          tools ─┘ (loop back to agent)

Hop cap
=======
Hard limit of `MAX_TOOL_HOPS` tool round-trips per chat turn. Past that
the agent is forced to answer with whatever it already has. This is a
runaway-cost guardrail, not a quality knob: agents that legitimately need
>5 tool calls usually have a planning bug, and we'd rather degrade
gracefully than serve a $5 chat answer.

Auth boundary
=============
The agent NEVER sees `user_id` or `session_id`. Those live in the
`ToolContext` captured by `build_tools()` closures in services/tools.py.
The graph only sees a list of `StructuredTool`s already bound to the
caller's identity — i.e., a tool the LLM picks can only act as the
authenticated user.

Failure model
=============
A model error during the graph run propagates to the caller. The SSE
handler in api/chat.py catches and emits an `error` event — same shape
as the legacy single-shot path. A tool error surfaces as a ToolMessage
with `status="error"`; the agent typically recovers by trying a
different tool or answering from what it has.
"""
from __future__ import annotations

import logging
from typing import Annotated, Any, TypedDict
from uuid import UUID

from langchain_core.messages import AnyMessage, BaseMessage, SystemMessage
from langchain_google_vertexai import ChatVertexAI
from langgraph.graph import END, START, StateGraph
from langgraph.graph.message import add_messages
from langgraph.prebuilt import ToolNode

from core.config import settings
from services import observability as obs
from services.tools import ToolContext, build_tools

logger = logging.getLogger(__name__)


# Hard cap on tool round-trips per chat turn. See module docstring.
MAX_TOOL_HOPS: int = 5


# ── State ───────────────────────────────────────────────────────────────────


class AgentState(TypedDict):
    """LangGraph state for one chat turn.

    `messages` uses LangGraph's `add_messages` reducer so each node can
    append new messages without overwriting history. `hop_count` tracks
    how many tool round-trips we've done — incremented in the tools node
    so the conditional edge can route to END once we hit the cap.

    The auth fields (`user_id`, `session_id`) live here for tracing /
    logging only; tools read identity from their captured ToolContext,
    not from the state dict.
    """

    messages: Annotated[list[AnyMessage], add_messages]
    hop_count: int
    user_id: str
    session_id: str


# ── System prompt ───────────────────────────────────────────────────────────

SYSTEM_PROMPT = """You are the Taqreerk AI research assistant. You help users explore Arabic and English research reports from the GCC region.

You have access to a fixed toolset (search_chunks, get_page, list_reports, get_report_metadata, get_report_summary, get_report_indicators, get_report_trends, get_report_recommendations, get_report_keywords, get_translation, list_saved_reports, list_user_interests, find_similar_reports, get_session_history). Use them to ground every factual claim in retrieved data — do not answer from prior knowledge.

CORE PRINCIPLE — never refuse a question just because pre-baked data is missing.
Most of the structured tools (`get_report_keywords`, `get_report_summary`, `get_report_indicators`, `get_report_trends`, `get_report_recommendations`) return AI-generated content that may not have been computed yet for a given report. **An empty result from these tools does NOT mean the answer is unavailable — it just means the shortcut isn't ready.** The report's full text is always available via `search_chunks` and `get_page`.

When a structured tool returns empty, you MUST proceed to `search_chunks` in the SAME turn and synthesise the answer from the chunks yourself. Refusing to answer because the shortcut is empty is a failure. "I'll do it next time" / "I should have used search_chunks" is also a failure — do it NOW, in this same turn.

Worked example — DO follow this exact pattern:

  User: "ما هي الكلمات المفتاحية لهذا التقرير؟"

  Hop 1:  get_report_keywords(report_id=X)
          → {"results":[],"reason":"no keywords have been generated yet"}
  Hop 2:  get_report_metadata(report_id=X)
          → {"title":"تقرير الطاقة في السعودية 2024","sector":"الطاقة","country":"SA","year":2024,...}
  Hop 3:  search_chunks(query="الطاقة المتجددة النفط رؤية 2030 السعودية",
                        report_id=X, top_k=8)
          → {"results":[{"page":3,"content":"..."}, ...]}

  Final answer (you write this from the chunks):
    "الكلمات المفتاحية الرسمية لم تُنشأ بعد لهذا التقرير، لكن من محتواه
     يتضح أن أبرز الموضوعات هي:
       • الطاقة المتجددة (الصفحات 3، 12)
       • رؤية 2030 (الصفحات 7، 18)
       • تنويع مصادر النفط (الصفحات 22-24)
       • كفاءة الطاقة (الصفحات 31-33)"

Anti-example — DO NOT do this:

  User: "ما هي الكلمات المفتاحية لهذا التقرير؟"
  Hop 1:  get_report_keywords(report_id=X) → empty
  Final: "لم يتم إنشاء كلمات مفتاحية لهذا التقرير بعد."   ← WRONG. Refused
                                                              with hops to spare.

Fallback ladder (use in this order):

  1. Structured shortcut — fastest, pre-written:
       keywords?       → get_report_keywords
       summary?        → get_report_summary
       KPIs / numbers? → get_report_indicators
       trends?         → get_report_trends
       recommendations?→ get_report_recommendations

  2. If step 1 returned `reason: "...not generated yet..."` (or empty), do NOT stop. Try another structured tool that IS pre-baked for this report (often `get_report_summary` is filled even when keywords aren't), then fall back to step 3 if that's empty too.

  3. Raw content fallback — always works for any Published / accessible report:
       → First, if you don't already know the report's topic, call
         `get_report_metadata(report_id)` to learn its title, sector,
         country, year. THIS IS CRITICAL for choosing a good search query.
       → Then call:
         search_chunks(query=<CONTENT-BEARING TERMS, not the user's
                              meta-question>,
                       report_id=<the report you're answering about>,
                       top_k=5..8)
       → Read the returned chunks and synthesise the answer yourself.

     CRITICAL — query selection for search_chunks:
       The query must contain words that ACTUALLY APPEAR in the report's
       content. Do NOT use the user's meta-question literally. Examples:

         User asks                Report is about energy in KSA 2024
         ─────────────────────────────────────────────────────────────
         "what are the keywords"     →  search query: "energy oil renewable
                                                       Saudi 2024 Vision"
                                        (NOT: "keywords")
         "main topics?"               →  search query: title + sector terms
                                        (NOT: "main topics")
         "what is the summary?"       →  search query: title + a few key
                                                       terms from metadata
                                        (NOT: "summary")
         "give me the recommendations" → search query: "recommendations
                                                       policy actions"
                                        (this IS a content term —
                                         reports do contain that word)

       Rule of thumb: if the word the user used is a META term ("keywords",
       "topics", "summary", "main points"), DO NOT pass it as the query.
       Replace it with terms drawn from the report's metadata (title /
       sector / country / year) and likely subject-matter vocabulary.

       After you have the chunks, you write the answer in your own words.
       For "keywords / topics", extract the most-mentioned nouns and
       themes from what the chunks actually say.

  4. Last resort — only if step 3 is also empty (no chunks indexed for the
     report at all, even with a sensible query):
       Tell the user honestly that the report hasn't been processed yet
       and to check back later.

MULTI-TURN QUERY REFORMULATION — do this BEFORE calling search_chunks:
When the user's message contains pronouns or back-references ("it", "that",
"هذا", "نفس", "المذكور", "the same", "mentioned earlier", "from before",
"what about", "and the"), rewrite the search query into a fully self-contained
form that includes the referenced entity explicitly.

  Bad:  user says "what were the statistics mentioned?"
        → search_chunks(query="statistics mentioned")   ← retrieves nothing
  Good: history shows discussion of renewable energy in Saudi Arabia 2024
        → search_chunks(query="renewable energy statistics Saudi Arabia 2024") ← retrieves correctly

  Bad:  user says "what does it recommend?"
        → search_chunks(query="recommend it")           ← retrieves nothing
  Good: prior turn was about a specific energy report
        → search_chunks(query="recommendations energy sector report") ← correct

The search_chunks `prev_content` and `next_content` fields in results are the
chunks immediately before/after the match on the same page. Read them to
understand the full context of a result before quoting it.

Tool-call discipline:
  • NEVER call the same tool with the same arguments twice in one turn. The result is cached and you'll get a stop-message back. If a tool's first result is empty, the data isn't going to appear on retry — move down the fallback ladder.
  • Read each tool response as JSON. The presence of a `reason` field means the result is empty with an explanation. Use the explanation to choose your next move (usually: drop to `search_chunks`).
  • A response containing "failed with an internal error" is a real failure — tell the user once, briefly, and stop calling that tool. Try a different angle.
  • Hop budget: 5 tool calls maximum per turn. Spend them deliberately.
  • search_chunks now accepts an optional `block_types` argument. Use ["table"] when the user asks for numbers, KPIs, or statistics. Use ["figure"] for charts or graphs. Omit for general questions.

Selection guidelines:
  • Prefer the structured shortcut tool when it works — it's faster and the answer is editorial-grade. But always be ready to fall back to `search_chunks`.
  • If the user mentions a report by name but you don't have its id, call `list_reports` first with a keyword filter to resolve the id before any per-report tool.
  • Cite the source(s) you used: report title and page numbers when the answer comes from `search_chunks` / `get_page`.

Other rules:
  • PDFs are NEVER downloadable through chat. `get_translation` returns translated text only; if a user asks for a download, explain that translated text is available inline.
  • Respond in the same language the user used (Arabic ↔ English). If they mix, default to the dominant language in their last message.
"""


# ── Model factory ───────────────────────────────────────────────────────────


def _make_model(tools: list[Any]) -> ChatVertexAI:
    """Bind tools to a Vertex Gemini chat model.

    `temperature=0.2` matches the legacy single-shot chat path — low enough
    to keep tool selection deterministic, high enough that natural-language
    answers don't read like a stub. `max_output_tokens` is generous because
    Arabic answers tokenise heavier than English; capping too low truncates
    legitimate answers."""
    model = ChatVertexAI(
        model=settings.gemini_chat_model,
        project=settings.gcp_project_id,
        location=settings.vertex_location,
        temperature=0.2,
        max_output_tokens=2048,
    )
    return model.bind_tools(tools)


# ── Nodes ───────────────────────────────────────────────────────────────────


def _ensure_system_prompt(messages: list[BaseMessage]) -> list[BaseMessage]:
    """Prepend the system prompt if the caller didn't already include one.

    Idempotent — safe to call on every agent hop. We don't mutate the
    state list; we hand the model a fresh list so the system message
    doesn't leak back into the reducer-managed history.
    """
    if messages and isinstance(messages[0], SystemMessage):
        return messages
    return [SystemMessage(content=SYSTEM_PROMPT), *messages]


def _build_agent_node(model: ChatVertexAI):
    """Return the `agent` node closure bound to a tool-aware model."""

    async def agent_node(state: AgentState) -> dict[str, Any]:
        messages = _ensure_system_prompt(list(state["messages"]))
        response = await model.ainvoke(messages)
        return {"messages": [response]}

    return agent_node


async def _tools_node_with_counter(state: AgentState, tool_node: ToolNode) -> dict[str, Any]:
    """Wrap LangGraph's prebuilt ToolNode so we can increment hop_count.

    ToolNode itself returns just `{"messages": [...]}`; we add the counter
    bump in the same dict so the state reducer applies both in one step.
    """
    out = await tool_node.ainvoke(state)
    hops = state.get("hop_count", 0) + 1
    out = {**out, "hop_count": hops}
    return out


# ── Routing ─────────────────────────────────────────────────────────────────


def _route_after_agent(state: AgentState) -> str:
    """Decide whether to run tools again or stop.

    Stop when: (a) the model didn't ask for a tool, or (b) we already hit
    the hop cap. The cap is enforced by routing to END *before* the tools
    node runs — if we let the tools node run and then check the cap, the
    agent could still emit one extra round-trip per turn.
    """
    last = state["messages"][-1] if state["messages"] else None
    tool_calls = getattr(last, "tool_calls", None) if last is not None else None
    if not tool_calls:
        return END
    if state.get("hop_count", 0) >= MAX_TOOL_HOPS:
        logger.warning(
            "[agent] hop cap %d reached for session=%s — forcing END",
            MAX_TOOL_HOPS, state.get("session_id"),
        )
        return END
    return "tools"


# ── Graph build ─────────────────────────────────────────────────────────────


def build_agent(ctx: ToolContext):
    """Compile a LangGraph agent bound to the given per-turn ToolContext.

    Caller pattern (see api/chat.py):

        graph = build_agent(ctx)
        async for event in graph.astream_events(
            {"messages": history + [HumanMessage(content=user_msg)],
             "hop_count": 0,
             "user_id": str(ctx.user_id),
             "session_id": str(ctx.session_id)},
            config={"callbacks": [langfuse_handler]},
            version="v2",
        ):
            ...  # map to SSE

    The graph is rebuilt per chat turn — building is cheap (no model
    weights to load; ChatVertexAI lazy-inits its gRPC client on first
    call) and per-turn binding is what keeps `ctx` from leaking across
    requests.
    """
    tools = build_tools(ctx)
    model = _make_model(tools)

    tool_node = ToolNode(tools)

    async def tools_with_counter(state: AgentState) -> dict[str, Any]:
        return await _tools_node_with_counter(state, tool_node)

    graph = StateGraph(AgentState)
    graph.add_node("agent", _build_agent_node(model))
    graph.add_node("tools", tools_with_counter)

    graph.add_edge(START, "agent")
    graph.add_conditional_edges(
        "agent",
        _route_after_agent,
        {"tools": "tools", END: END},
    )
    graph.add_edge("tools", "agent")

    return graph.compile()


# ── Public helpers for the SSE handler ─────────────────────────────────────


def initial_state(
    *,
    user_id: UUID | str,
    session_id: UUID | str,
    history: list[BaseMessage],
    user_message: BaseMessage,
) -> AgentState:
    """Build the starting AgentState for one chat turn.

    `history` should already be in LangChain message format (HumanMessage /
    AIMessage instances) — the api/chat.py layer is responsible for
    converting from the DB's `chat_messages` rows.
    """
    return {
        "messages": [*history, user_message],
        "hop_count": 0,
        "user_id": str(user_id),
        "session_id": str(session_id),
    }


def make_langfuse_handler(
    *,
    user_id: UUID | str,
    session_id: UUID | str,
    trace_id: str | None = None,
    trace_name: str = "chat_agent",
) -> Any | None:
    """Return a Langfuse LangChain CallbackHandler, or None when disabled.

    Two construction paths depending on whether the caller pinned a trace
    id upfront:

      • `trace_id` provided  → pre-create the trace via the Langfuse SDK
        client (`client.trace(id=...)`) and return that trace's
        `get_langchain_handler()`. The agent's spans nest under our chosen
        id, AND the async Ragas eval worker can later post `score` events
        against the same id so quality metrics show up inline in the
        Langfuse dashboard. This is the production path.

      • no `trace_id`        → fall back to a vanilla `CallbackHandler`
        which invents its own id. Scores from the eval worker would be
        orphaned in this mode, so we only use this for local debug runs.

    Why we don't pass `trace_id` to `CallbackHandler(...)` directly: the
    Langfuse 2.x CallbackHandler constructor doesn't accept a `trace_id`
    kwarg, so doing so raises TypeError, the surrounding try/except
    swallows it, and the agent runs with NO Langfuse callback at all.
    That's the bug we're fixing here — the previous version of this
    function silently disabled Langfuse on every chat that pinned a
    trace id.
    """
    if not settings.langfuse_enabled:
        return None
    if not (settings.langfuse_host
            and settings.langfuse_public_key
            and settings.langfuse_secret_key):
        return None

    try:
        if trace_id:
            # Pre-create the trace so we own its id. The trace object
            # carries identity (user/session/tags) and `get_langchain_handler`
            # returns a CallbackHandler bound to it — every span the agent
            # emits via config={"callbacks": [handler]} attaches here.
            client = obs.get_client()
            if client is None:
                # Langfuse client init failed earlier; fall through to
                # the no-handler path so the agent still runs.
                return None
            trace = client.trace(
                id=trace_id,
                name=trace_name,
                user_id=str(user_id),
                session_id=str(session_id),
                tags=["chat", "agent"],
            )
            return trace.get_langchain_handler(update_parent=False)

        # No pinned id — vanilla handler with auto-generated trace.
        from langfuse.callback import CallbackHandler  # type: ignore[import-not-found]
        return CallbackHandler(
            host=settings.langfuse_host,
            public_key=settings.langfuse_public_key,
            secret_key=settings.langfuse_secret_key,
            user_id=str(user_id),
            session_id=str(session_id),
            trace_name=trace_name,
            tags=["chat", "agent"],
        )
    except Exception as exc:
        logger.warning("[agent] langfuse handler init failed: %s", exc)
        return None
