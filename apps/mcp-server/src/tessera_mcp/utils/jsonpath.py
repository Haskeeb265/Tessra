"""A minimal JSONPath evaluator for manifest `response_mapping` values.

The sample manifests use paths like ``$.data.appointment``,
``$.data.appointments`` and ``$.data.cancellation``. This implements the
subset needed for the manifest contract:

- ``$`` root
- ``.field`` and ``['field']`` child access
- ``[n]`` list index
- ``[*]`` wildcard over a list

Anything else raises :class:`JsonPathError`. A full JSONPath implementation
(e.g. jsonpath-ng) can replace this later if the contract grows.
"""

from __future__ import annotations

import re
from typing import Any

_TOKEN = re.compile(
    r"""
    \s*
    (
        \.(?P<name>[A-Za-z_][A-Za-z0-9_]*)
        |
        \[\s*(?P<index>[*]|-?\d+)\s*\]
        |
        \[\s*'(?P<quoted>(?:[^'\\]|\\.)*)'\s*\]
    )
    """,
    re.VERBOSE,
)


class JsonPathError(ValueError):
    pass


def evaluate(path: str, document: Any) -> Any:
    """Evaluate ``path`` against ``document`` (already parsed JSON)."""
    if not path.startswith("$"):
        raise JsonPathError(f"response_mapping must start with '$': {path!r}")

    current = document
    rest = path[1:]
    pos = 0
    # True once a [*] wildcard expanded the current value into a list; a
    # following .field / [n] then maps over each element.
    wildcard = False

    while pos < len(rest):
        match = _TOKEN.match(rest, pos)
        if match is None:
            raise JsonPathError(
                f"Unsupported response_mapping token at {rest[pos:]!r} "
                f"in {path!r}")
        pos = match.end()

        name = match.group("name")
        index = match.group("index")
        quoted = match.group("quoted")

        if name is not None:
            if wildcard:
                if not isinstance(current, list):
                    raise JsonPathError(
                        f"Cannot apply {name!r} to non-list at {path!r}")
                current = [_child(item, name, path) for item in current]
            else:
                current = _child(current, name, path)
        elif index is not None:
            if index == "*":
                if isinstance(current, list):
                    # One level of flattening: [*] over a list of lists
                    # yields the inner elements.
                    current = [
                        item
                        for element in current
                        for item in (element if isinstance(element, list)
                                     else [element])
                    ]
                    wildcard = True
                else:
                    raise JsonPathError(
                        f"Cannot apply [*] to non-list at {path!r}")
            else:
                idx = int(index)
                if wildcard:
                    # Apply the index to the wildcard-expanded list itself.
                    current = _item(current, idx, path)
                else:
                    current = _item(current, idx, path)
        else:
            if wildcard:
                if not isinstance(current, list):
                    raise JsonPathError(
                        f"Cannot apply {quoted!r} to non-list at {path!r}")
                current = [_child(item, quoted, path) for item in current]
            else:
                current = _child(current, quoted, path)

    return current


def _child(document: Any, key: str, path: str) -> Any:
    if isinstance(document, dict):
        if key in document:
            return document[key]
        raise JsonPathError(
            f"Key {key!r} not found in document at {path!r}")
    raise JsonPathError(
        f"Cannot descend into non-object at {path!r}")


def _item(document: Any, index: int, path: str) -> Any:
    if isinstance(document, list) and -len(document) <= index < len(document):
        return document[index]
    raise JsonPathError(f"Index {index} out of range at {path!r}")