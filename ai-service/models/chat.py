from uuid import UUID
from pydantic import BaseModel


class SendMessageRequest(BaseModel):
    message: str


class CreateSessionRequest(BaseModel):
    user_id: UUID
    report_id: UUID
    title: str = "New Chat"


class CreateSessionResponse(BaseModel):
    session_id: UUID
    title: str


class SessionMessage(BaseModel):
    role: str   # "user" | "assistant"
    content: str
    source_pages: list[int] | None = None


class SessionHistoryResponse(BaseModel):
    session_id: UUID
    report_id: UUID
    title: str
    messages: list[SessionMessage]
