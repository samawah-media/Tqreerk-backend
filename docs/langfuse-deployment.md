# Langfuse setup — Cloud (managed)

We use **Langfuse Cloud** (their hosted SaaS) for observability. No
infrastructure to deploy on our side; the ai-service SDK posts traces to
their public ingest endpoint, and we view dashboards at cloud.langfuse.com.

## Why Cloud (vs self-hosting)

We considered three paths:

| Option | Verdict |
|---|---|
| Langfuse Cloud (managed) | ✅ chosen — zero ops, free tier covers staging, code is host-agnostic so we can move later |
| Self-host on a fresh Cloud Run + Cloud SQL | Doable but adds a 4th Cloud Run service + a Postgres bill for an internal tool. Not worth it at our scale. |
| Self-host on a GCE VM + docker-compose | Even simpler ops than Cloud Run, but requires a standing-on VM ($17/mo). Skip until/unless we need to. |

The *code path* in `services/observability.py` is identical regardless of
where Langfuse runs — only the `LANGFUSE_HOST` env var differs. If we ever
need to move off Cloud (compliance, cost, scale), it's a one-line workflow
change plus a fresh keypair.

## One-time setup

### 1. Create the project + keys

1. Sign up at https://cloud.langfuse.com (use Google SSO; pick **EU region**
   so the data centre is closer to me-central1 Cloud Run).
2. Create projects: `taqreerk-staging` and (later) `taqreerk-production`.
3. Project Settings → API Keys → Create:
   - `pk-lf-...` (public key)
   - `sk-lf-...` (secret key)

### 2. Add the 3 secrets to GitHub

Repo → Settings → Secrets and variables → Actions → New repository secret:

| Name | Value |
|---|---|
| `LANGFUSE_HOST` | `https://cloud.langfuse.com` |
| `LANGFUSE_PUBLIC_KEY` | the `pk-lf-...` key |
| `LANGFUSE_SECRET_KEY` | the `sk-lf-...` key |

The deploy workflows (`deploy-ai-service-staging.yml`,
`deploy-ai-service-production.yml`) already wire these into env vars on
both the API and worker Cloud Run services.

### 3. Verify after the next deploy

ai-service logs on the first chat:
```
[obs] langfuse client ready host=https://cloud.langfuse.com
chat[xxxxxxxx] enqueued eval job=... trace=...
```

Worker logs ~30-60s later:
```
[eval] trace=... elapsed=...ms scores={'faithfulness': 0.9x, ...}
```

Cloud dashboard: https://cloud.langfuse.com → your project → **Traces** —
each chat appears as a `chat` trace with sub-spans and the `gemini_chat`
generation. Scores attach asynchronously (they show up as small chips
under the trace once the eval worker finishes).

## Free-tier limits

- **50,000 events / month** — at ~14 events per chat (1 trace + 6 spans + 1
  generation + 6 scores), that's roughly 3,500 chats/month. Plenty for
  staging.
- Email warnings at 80% / 90% of quota.
- Once at 100%, ingest is rate-limited until the 1st of the next month.
  **Chat keeps working** — the SDK drops events silently, our wrapper
  catches any raised exception. Worst case: dashboard misses traces for a
  while.

## Tuning sample rates

Two knobs, both env vars on the ai-service / worker Cloud Run services:

| Var | Default staging | Default production | Purpose |
|---|---|---|---|
| `LANGFUSE_TRACE_SAMPLE_RATE` | `1.0` | `0.5` | Fraction of chats to TRACE. Drops trace + spans + generation events together. |
| `EVAL_SAMPLE_RATE` | `1.0` | `0.2` | Fraction of TRACED chats to ALSO eval (each eval adds 6 score events + judge LLM cost). |

If approaching 50k mid-month:

```bash
gcloud run services update taqreerk-ai-service-staging \
  --update-env-vars LANGFUSE_TRACE_SAMPLE_RATE=0.3,EVAL_SAMPLE_RATE=0.1 \
  --region me-central1
gcloud run services update taqreerk-ai-worker-staging \
  --update-env-vars LANGFUSE_TRACE_SAMPLE_RATE=0.3,EVAL_SAMPLE_RATE=0.1 \
  --region me-central1
```

(No redeploy — Cloud Run hot-swaps env vars in seconds.)

## Hard kill switch

To turn off Langfuse / eval entirely without redeploying code:

```bash
# disable tracing on the API
gcloud run services update taqreerk-ai-service-staging \
  --update-env-vars LANGFUSE_PUBLIC_KEY="" \
  --region me-central1
# (do the same for the worker)
```

The `services/observability.py` no-op fallback kicks in (`[obs] langfuse
keys/host missing; tracing skipped`) and chat continues to work. Same for
`EVAL_ENABLED=false` on the worker to skip the eval queue.

## Production rollout checklist

When ready to go live:

1. Create a SECOND Langfuse project: `taqreerk-production`.
2. Get a fresh keypair from that project (do NOT reuse staging keys).
3. Add three production-specific GitHub secrets in the same repo, e.g.
   `LANGFUSE_PUBLIC_KEY_PROD` / `LANGFUSE_SECRET_KEY_PROD`, and reference
   them from `deploy-ai-service-production.yml`.
4. Once volume is known, decide whether to upgrade to Langfuse Pro ($59/mo
   for 100k events) or stay on the free tier with sample rates tuned down.
