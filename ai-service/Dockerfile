FROM python:3.12-slim

WORKDIR /app

# System deps:
#   - libmupdf-dev: PyMuPDF (PDF rendering for ingest)
#   - tesseract-ocr + ara/eng + ghostscript/unpaper/qpdf/pngquant: OCRmyPDF
#     adds a real text layer to every PDF before translation, ensuring
#     path-rendered Arabic PDFs (government forms) translate reliably.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libmupdf-dev \
    tesseract-ocr \
    tesseract-ocr-ara \
    tesseract-ocr-eng \
    ghostscript \
    unpaper \
    qpdf \
    pngquant \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

ENV PYTHONUNBUFFERED=1
EXPOSE 8080

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
