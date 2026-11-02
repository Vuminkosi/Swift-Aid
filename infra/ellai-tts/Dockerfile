FROM ghcr.io/astral-sh/uv:0.13.5 AS uv

FROM python:3.12.15-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1

WORKDIR /app
COPY --from=uv /uv /uvx /bin/
COPY pyproject.toml uv.lock ./
COPY app.py voice_pool.py audio_processing.py ./

RUN addgroup --system swiftaid \
    && adduser --system --ingroup swiftaid swiftaid \
    && uv sync --frozen --no-dev --no-cache \
    && chown -R swiftaid:swiftaid /app

USER swiftaid
EXPOSE 8090
ENTRYPOINT ["/app/.venv/bin/uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8090"]
