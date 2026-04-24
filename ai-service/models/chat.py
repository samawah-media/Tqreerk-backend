from uuid import UUID
from pydantic import BaseModel


class SendMessageRequest(BaseModel):
    session_id: UUID
    message: str


class SendMessageResponse(BaseModel):
    session_id: UUID
    answer: str
    source_pages: list[int]


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
