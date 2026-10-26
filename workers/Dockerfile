FROM ghcr.io/astral-sh/uv:0.13.5 AS uv

FROM python:3.12.15-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy

WORKDIR /app
COPY --from=uv /uv /uvx /bin/
COPY pyproject.toml uv.lock ./
COPY src ./src

RUN addgroup --system swiftaid \
    && adduser --system --ingroup swiftaid swiftaid \
    && uv sync --frozen --no-dev --no-cache \
    && chown -R swiftaid:swiftaid /app

USER swiftaid
ENTRYPOINT ["/app/.venv/bin/python", "-m", "swiftaid_workers"]
