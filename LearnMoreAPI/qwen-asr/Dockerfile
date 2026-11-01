ARG BASE_IMAGE=learnmore-api:gb10
FROM ${BASE_IMAGE}

USER 0
WORKDIR /opt/qwen-asr
COPY requirements.txt .
# 保留 GB10 已驗證的 CUDA 套件，避免 pip 換成不相容的公開 wheel。
RUN python -c 'import importlib.metadata as m; print("\n".join(p+"=="+m.version(p) for p in ("torch", "torchaudio", "numpy", "transformers")))' > /tmp/qwen-base-constraints.txt \
    && python -m pip install --no-cache-dir -c /tmp/qwen-base-constraints.txt -r requirements.txt \
    && mkdir -p /models /work \
    && chown 1000:1000 /models /work
COPY qwen3_asr_cli.py .
ENV HF_HOME=/models \
    HF_HUB_DISABLE_IMPLICIT_TOKEN=1 \
    HF_HUB_DISABLE_XET=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
USER 1000:1000
ENTRYPOINT ["python", "/opt/qwen-asr/qwen3_asr_cli.py"]
CMD ["doctor"]
