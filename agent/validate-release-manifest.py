#!/usr/bin/env python3
"""Fail-closed validator for the JSON-Schema subset used by release manifests."""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import re
import sys
from typing import Any

SUPPORTED = {
    "$schema", "$id", "title", "description",
    "type", "additionalProperties", "required", "properties",
    "const", "pattern", "minLength", "format", "minProperties",
}
RFC3339 = re.compile(
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}"
    r"(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$"
)

class ValidationError(RuntimeError):
    pass

def load_json(path: str) -> Any:
    def no_duplicates(pairs):
        out = {}
        for key, value in pairs:
            if key in out:
                raise ValidationError(f"duplicate JSON object key: {key}")
            out[key] = value
        return out
    try:
        return json.loads(pathlib.Path(path).read_text(encoding="utf-8"), object_pairs_hook=no_duplicates)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValidationError(f"cannot parse JSON document {path}: {exc}") from exc

def check_schema(schema: Any, path: str = "$") -> None:
    if isinstance(schema, bool):
        return
    if not isinstance(schema, dict):
        raise ValidationError(f"{path}: schema node must be object or boolean")
    unknown = set(schema) - SUPPORTED
    if unknown:
        raise ValidationError(f"{path}: unsupported schema keyword(s): {sorted(unknown)}")
    typ = schema.get("type")
    if typ is not None and typ not in {"object", "string"}:
        raise ValidationError(f"{path}: unsupported type declaration: {typ!r}")
    props = schema.get("properties", {})
    if not isinstance(props, dict):
        raise ValidationError(f"{path}.properties must be an object")
    for name, child in props.items():
        check_schema(child, f"{path}.properties[{name!r}]")
    additional = schema.get("additionalProperties", True)
    if not isinstance(additional, bool):
        check_schema(additional, f"{path}.additionalProperties")
    required = schema.get("required", [])
    if not isinstance(required, list) or any(not isinstance(x, str) for x in required):
        raise ValidationError(f"{path}.required must be an array of strings")

def is_type(value: Any, typ: str) -> bool:
    if typ == "object":
        return isinstance(value, dict)
    if typ == "string":
        return isinstance(value, str)
    raise AssertionError(typ)

def validate_datetime(value: str, path: str) -> None:
    if not RFC3339.fullmatch(value):
        raise ValidationError(f"{path}: invalid RFC3339 date-time")
    try:
        parsed = dt.datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)
    except ValueError as exc:
        raise ValidationError(f"{path}: invalid date-time") from exc
    if parsed.tzinfo is None:
        raise ValidationError(f"{path}: date-time must include an offset")

def validate(schema: Any, value: Any, path: str = "$") -> None:
    if schema is False:
        raise ValidationError(f"{path}: forbidden by schema")
    if schema is True:
        return

    if "const" in schema and value != schema["const"]:
        raise ValidationError(f"{path}: value does not match const")

    typ = schema.get("type")
    if typ is not None and not is_type(value, typ):
        raise ValidationError(f"{path}: expected {typ}")

    if isinstance(value, dict):
        minimum = schema.get("minProperties")
        if minimum is not None:
            if not isinstance(minimum, int) or isinstance(minimum, bool) or minimum < 0:
                raise ValidationError(f"{path}: invalid minProperties in schema")
            if len(value) < minimum:
                raise ValidationError(f"{path}: requires at least {minimum} properties")

        required = schema.get("required", [])
        missing = [name for name in required if name not in value]
        if missing:
            raise ValidationError(f"{path}: missing required properties: {missing}")

        props = schema.get("properties", {})
        additional = schema.get("additionalProperties", True)
        for key, child in value.items():
            if key in props:
                validate(props[key], child, f"{path}.{key}")
            elif additional is False:
                raise ValidationError(f"{path}: additional property forbidden: {key}")
            elif isinstance(additional, dict):
                validate(additional, child, f"{path}.{key}")

    if isinstance(value, str):
        minimum = schema.get("minLength")
        if minimum is not None:
            if not isinstance(minimum, int) or isinstance(minimum, bool) or minimum < 0:
                raise ValidationError(f"{path}: invalid minLength in schema")
            if len(value) < minimum:
                raise ValidationError(f"{path}: string shorter than {minimum}")

        pattern = schema.get("pattern")
        if pattern is not None:
            if not isinstance(pattern, str):
                raise ValidationError(f"{path}: invalid pattern in schema")
            try:
                matched = re.search(pattern, value)
            except re.error as exc:
                raise ValidationError(f"{path}: invalid regex in schema: {exc}") from exc
            if matched is None:
                raise ValidationError(f"{path}: string does not match pattern")

        fmt = schema.get("format")
        if fmt is not None:
            if fmt != "date-time":
                raise ValidationError(f"{path}: unsupported format in schema: {fmt}")
            validate_datetime(value, path)

def main() -> int:
    if len(sys.argv) != 3:
        print(f"usage: {sys.argv[0]} SCHEMA.json DOCUMENT.json", file=sys.stderr)
        return 2
    try:
        schema = load_json(sys.argv[1])
        document = load_json(sys.argv[2])
        check_schema(schema)
        validate(schema, document)
    except ValidationError as exc:
        print(f"release manifest validation failed: {exc}", file=sys.stderr)
        return 1
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
