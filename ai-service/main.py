from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from api.chat import router as chat_router
from api.reports import router as reports_router

app = FastAPI(title="Taqreerk AI Service", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(chat_router,    prefix="/api/ai")
app.include_router(reports_router, prefix="/api/ai")


@app.get("/healthz")
async def health():
    return {"status": "healthy"}
