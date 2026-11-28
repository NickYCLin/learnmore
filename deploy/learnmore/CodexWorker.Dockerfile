FROM codex-base

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
RUN pip install --no-cache-dir pymssql==2.3.11 \
    && install -d -o 1000 -g 1000 -m 0700 /var/lib/learnmore-codex-worker
COPY scripts/learnmore_codex_backfill.py scripts/learnmore_codex_worker.py ./
USER 1000:1000
CMD ["python", "learnmore_codex_worker.py"]
