FROM python:3.12-slim

WORKDIR /app

# System deps for PyMuPDF
RUN apt-get update && apt-get install -y --no-install-recommends \
    libmupdf-dev \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

ENV PYTHONUNBUFFERED=1
EXPOSE 8080



# Single image, two roles. The Cloud Run service sets WORKER_MODE per-deployment:
#   • WORKER_MODE=api    → FastAPI server on $PORT (default 8080)
#   • WORKER_MODE=worker → polling loop over ai_jobs (FOR UPDATE SKIP LOCKED)
#
# The worker still binds to $PORT to satisfy Cloud Run's health-probe contract,
# but its real work happens inside the asyncio loop started by main.py. The
# health endpoint reflects the active mode via /health.
ENV WORKER_MODE=api


CMD ["sh", "-c", "if [ \"$WORKER_MODE\" = \"worker\" ]; then python main.py & uvicorn main:app --host 0.0.0.0 --port ${PORT:-8080} --log-level warning; else uvicorn main:app --host 0.0.0.0 --port ${PORT:-8080}; fi"]
