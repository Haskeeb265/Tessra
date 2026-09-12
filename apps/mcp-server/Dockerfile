# MCP gateway (apps/mcp-server). The local Acme Dental stub backend uses this
# same image with a different command — see docker-compose.yml.
FROM python:3.13-slim

# uv, matching the local dev workflow (pyproject.toml + uv.lock).
COPY --from=ghcr.io/astral-sh/uv:latest /uv /uvx /bin/

ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

WORKDIR /app

# Dependencies first, so editing sources doesn't invalidate the cached layer.
COPY pyproject.toml uv.lock README.md ./
RUN uv sync --no-dev --no-install-project

COPY src ./src
COPY stub_backend ./stub_backend

# Installs the project itself (hatchling) on top of the cached dependencies.
RUN uv sync --no-dev

ENV PATH="/app/.venv/bin:$PATH"

# 8000 = gateway, 9100 = stub backend.
EXPOSE 8000 9100

CMD ["uvicorn", "tessera_mcp.main:app", "--host", "0.0.0.0", "--port", "8000"]
