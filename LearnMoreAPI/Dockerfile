FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HF_HOME=/home/learnmore/.cache/huggingface \
    LD_LIBRARY_PATH=/usr/local/lib/python3.12/site-packages/nvidia/cublas/lib:/usr/local/lib/python3.12/site-packages/nvidia/cudnn/lib

WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg nodejs \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY learnmore_api ./learnmore_api

EXPOSE 8080
RUN mkdir -p /home/learnmore/.cache/huggingface /tmp/learnmore-api-codex /tmp/learnmore-api-whisper /tmp/learnmore-api-uvr \
    && chown -R 1000:1000 /home/learnmore /tmp/learnmore-api-codex /tmp/learnmore-api-whisper /tmp/learnmore-api-uvr
USER 1000:1000
CMD ["uvicorn", "learnmore_api.main:app", "--host", "0.0.0.0", "--port", "8080", "--proxy-headers"]
