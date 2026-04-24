import logging

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="Taqreerk AI Service", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health")
async def health():
    return {"status": "healthy"}


# Routers imported after /healthz so health check always responds even if imports fail
try:
    from api.chat import router as chat_router
    from api.reports import router as reports_router

    app.include_router(chat_router,    prefix="/api/ai")
    app.include_router(reports_router, prefix="/api/ai")
    logger.info("Routers loaded successfully.")
except Exception as exc:
    logger.error(f"Failed to load routers: {exc}", exc_info=True)
