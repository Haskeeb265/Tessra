"""Tests for the tiny JSONPath subset used by response_mapping."""

from __future__ import annotations

import pytest

from tessera_mcp.utils.jsonpath import JsonPathError, evaluate


def test_root_reference_returns_whole_document():
    payload = {"data": {"appointment": {"id": "abc"}}}
    assert evaluate("$", payload) is payload


def test_simple_path():
    payload = {"data": {"appointment": {"id": "abc"}}}
    assert evaluate("$.data.appointment.id", payload) == "abc"


def test_array_index():
    payload = {"data": {"appointments": [{"id": "a"}, {"id": "b"}]}}
    assert evaluate("$.data.appointments[1].id", payload) == "b"


def test_path_that_resolves_to_array_returns_list():
    payload = {"data": {"appointments": [{"id": "a"}]}}
    assert evaluate("$.data.appointments", payload) == [{"id": "a"}]


def test_wildcard_over_list():
    payload = {"data": {"items": [{"id": "a"}, {"id": "b"}]}}
    assert evaluate("$.data.items[*].id", payload) == ["a", "b"]


def test_missing_key_raises_jsonpath_error():
    payload = {"data": {}}
    with pytest.raises(JsonPathError):
        evaluate("$.data.nope", payload)


def test_missing_array_index_raises():
    payload = {"data": {"items": []}}
    with pytest.raises(JsonPathError):
        evaluate("$.data.items[3]", payload)


def test_malformed_segment_raises():
    with pytest.raises(JsonPathError):
        evaluate("$.data..appointment", {"data": {}})


def test_path_without_root_raises():
    with pytest.raises(JsonPathError):
        evaluate("data.appointment", {"data": {}})