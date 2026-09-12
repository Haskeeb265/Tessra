"""Smoke tests for the local Acme Dental stub backend."""

from __future__ import annotations

import httpx2
import pytest

from stub_backend.app import API_KEY


@pytest.mark.asyncio
async def test_health(stub_client: httpx2.AsyncClient):
    response = await stub_client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


@pytest.mark.asyncio
async def test_book_then_list_then_cancel(stub_client: httpx2.AsyncClient):
    headers = {"X-Api-Key": API_KEY}
    await stub_client.post("/v1/reset")

    booked = await stub_client.post(
        "/v1/appointments",
        json={
            "patient": "Pat",
            "preferred_time": "2026-12-01T10:00:00Z",
            "service": "checkup",
        },
        headers=headers,
    )
    assert booked.status_code == 201
    appointment_id = booked.json()["data"]["appointment"]["id"]

    listed = await stub_client.get(
        "/v1/appointments", params={"patient": "Pat"}, headers=headers)
    assert listed.status_code == 200
    assert [a["id"] for a in listed.json()["data"]["appointments"]] == [
        appointment_id
    ]

    cancelled = await stub_client.delete(
        f"/v1/appointments/{appointment_id}", headers=headers)
    assert cancelled.status_code == 200
    assert cancelled.json()["data"]["cancellation"]["status"] == "cancelled"


@pytest.mark.asyncio
async def test_missing_api_key_is_401(stub_client: httpx2.AsyncClient):
    response = await stub_client.get("/v1/appointments")
    assert response.status_code == 401


@pytest.mark.asyncio
async def test_empty_patient_is_400(stub_client: httpx2.AsyncClient):
    response = await stub_client.post(
        "/v1/appointments",
        json={"patient": "", "preferred_time": "2026-12-01T10:00:00Z"},
        headers={"X-Api-Key": API_KEY},
    )
    assert response.status_code == 400