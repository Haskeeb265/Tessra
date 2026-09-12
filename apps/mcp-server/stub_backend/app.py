"""Local Acme Dental sample SMB backend (the fake api.acmedental.test).

The seeded manifests (docs/sample-smb/acme-dental-manifest.json) point at
https://api.acmedental.test/v1/... which does not exist; the gateway's host
override map routes those calls to this stub during local development and
tests. It implements exactly the three operations the sample manifests
declare: book, list and cancel appointments, protected by an api key header.

Run:
    uv run uvicorn stub_backend.app:app --host 0.0.0.0 --port 9100
"""

from __future__ import annotations

import uuid
from datetime import datetime, timezone

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route

# Must match the gateway's CREDENTIALS default for
# vault://acme-dental/booking-api-key (tessera_mcp/config.py).
API_KEY = "dev-acme-api-key-123"

# appointment_id -> appointment dict (process-local, dev only)
_STORE: dict[str, dict] = {}


def _authorized(request: Request) -> bool:
    return request.headers.get("X-Api-Key") == API_KEY


async def health(request: Request) -> JSONResponse:
    return JSONResponse({"status": "ok"})


async def list_appointments(request: Request) -> JSONResponse:
    if not _authorized(request):
        return JSONResponse({"error": "unauthorized"}, status_code=401)

    patient = request.query_params.get("patient")
    items = [
        appointment
        for appointment in _STORE.values()
        if not patient or appointment["patient"] == patient
    ]
    items.sort(key=lambda a: str(a.get("preferred_time", "")))
    return JSONResponse({"data": {"appointments": items}})


async def book_appointment(request: Request) -> JSONResponse:
    if not _authorized(request):
        return JSONResponse({"error": "unauthorized"}, status_code=401)

    body = await request.json()
    patient = body.get("patient")

    if not patient:
        return JSONResponse({"error": "patient is required"}, status_code=400)

    appointment = {
        "id": uuid.uuid4().hex[:12],
        "patient": patient,
        "preferred_time": body.get("preferred_time"),
        "service": body.get("service"),
        "status": "booked",
        "created_at": datetime.now(timezone.utc).isoformat(),
        # Dev-only echo so tests can verify the gateway injected the
        # configured credential rather than the caller's own key.
        "authenticated_via": request.headers.get("X-Api-Key"),
    }
    _STORE[appointment["id"]] = appointment

    return JSONResponse(
        {"data": {"appointment": appointment}}, status_code=201)


async def cancel_appointment(request: Request) -> JSONResponse:
    if not _authorized(request):
        return JSONResponse({"error": "unauthorized"}, status_code=401)

    appointment_id = request.path_params["appointment_id"]
    appointment = _STORE.get(appointment_id)

    if appointment is None:
        return JSONResponse({"error": "not found"}, status_code=404)

    appointment["status"] = "cancelled"

    return JSONResponse({
        "data": {
            "cancellation": {
                "id": appointment_id,
                "status": "cancelled",
            }
        }
    })


async def reset(request: Request) -> JSONResponse:
    """Dev-only: clear the in-memory store (used by E2E scripts)."""
    _STORE.clear()
    return JSONResponse({"reset": True})


app = Starlette(
    routes=[
        Route("/health", health),
        Route("/v1/appointments", list_appointments, methods=["GET"]),
        Route("/v1/appointments", book_appointment, methods=["POST"]),
        Route(
            "/v1/appointments/{appointment_id}",
            cancel_appointment,
            methods=["DELETE"],
        ),
        Route("/v1/reset", reset, methods=["POST"]),
    ]
)