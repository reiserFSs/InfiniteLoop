#!/usr/bin/env python3
"""Index retail protocol schemas, Lua consumers, captures, and AscNet coverage."""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Iterable

REPO = Path(__file__).resolve().parents[1]
DEFAULT_METADATA = REPO.parent / "il2cpp/dumps/il2cppdumper-runtime-metadata/dump.cs"
DEFAULT_LUA = REPO.parent / "PGR_DATA/en/lua/matrix"
BOOTSTRAP = {"HandshakeRequest", "LoginRequest", "ReconnectRequest", "ClientVersionRequest", "SetServerBeanRequest"}
CLASS_RE = re.compile(
    r"// Namespace: Protocol\.Protocol\.Frontend\n"
    r"(?P<attrs>(?:\[[^\n]+\]\n)*)public (?:sealed )?class (?P<name>\w+)[^{]*\n\{(?P<body>.*?)\n\}",
    re.S,
)
FIELD_RE = re.compile(r"^\s*public (?!static\b|const\b)(?P<type>.+?) (?P<name>[A-Za-z_]\w*);(?: //.*)?$", re.M)
CALL_RE = re.compile(r"XNetwork\.(?:Call(?:WithAutoHandleErrorCode)?|Send)\s*\(")
RPC_RE = re.compile(r"XRpc\.(?P<name>\w+)\s*=\s*function\s*\(\s*(?P<arg>\w*)")
ADD_RPC_RE = re.compile(r"(?:self|[A-Za-z_]\w*):AddRpc\(\s*[\"'](?P<name>\w+)[\"']\s*,\s*handler\([^,]+,\s*[^.]+\.(?P<method>\w+)\)")


def parse_metadata(path: Path) -> dict[str, list[str]]:
    text = path.read_text(encoding="utf-8", errors="ignore")
    schemas: dict[str, list[str]] = {}
    for match in CLASS_RE.finditer(text):
        if "MessagePackObject" not in match["attrs"]:
            continue
        fields_match = re.search(r"// Fields\n(?P<fields>.*?)\n\s*// Methods", match["body"], re.S)
        fields = [] if fields_match is None else [
            f"{field['name']}:{field['type']}" for field in FIELD_RE.finditer(fields_match["fields"])
        ]
        schemas[match["name"]] = fields
    return schemas


def closing_paren(text: str, opening: int) -> int:
    depth = 0
    quote = ""
    escaped = False
    i = opening
    while i < len(text):
        char = text[i]
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = ""
        elif char in "\"'":
            quote = char
        elif text.startswith("--", i):
            newline = text.find("\n", i + 2)
            i = len(text) if newline < 0 else newline
            continue
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return min(len(text), opening + 4000)


def lua_constants(text: str) -> dict[str, str]:
    constants = {
        name: value
        for name, value in re.findall(r"(?:local\s+)?([A-Za-z_]\w*)\s*=\s*[\"']([A-Za-z]\w*Request)[\"']", text)
    }
    for table in re.finditer(r"(?:local\s+)?([A-Za-z_]\w*)\s*=\s*\{(?P<body>.*?)\}", text, re.S):
        for key, value in re.findall(r"([A-Za-z_]\w*)\s*=\s*[\"']([A-Za-z]\w*Request)[\"']", table["body"]):
            constants[f"{table.group(1)}.{key}"] = value
    return constants


def first_argument(call: str, constants: dict[str, str]) -> str | None:
    body = call[call.find("(") + 1 :]
    literal = re.match(r"\s*[\"']([A-Za-z]\w*Request)[\"']", body)
    if literal:
        return literal.group(1)
    reference = re.match(r"\s*([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)?)", body)
    return constants.get(reference.group(1)) if reference else None


def split_call_arguments(call: str) -> list[str]:
    opening = call.find("(")
    arguments: list[str] = []
    start = opening + 1
    parens = braces = brackets = 0
    quote = ""
    escaped = False
    i = opening
    while i < len(call):
        char = call[i]
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = ""
        elif char in "\"'":
            quote = char
        elif call.startswith("--", i):
            newline = call.find("\n", i + 2)
            i = len(call) if newline < 0 else newline
            continue
        elif char == "(":
            parens += 1
        elif char == ")":
            parens -= 1
            if parens == 0:
                arguments.append(call[start:i].strip())
                break
        elif char == "{":
            braces += 1
        elif char == "}":
            braces -= 1
        elif char == "[":
            brackets += 1
        elif char == "]":
            brackets -= 1
        elif char == "," and parens == 1 and braces == 0 and brackets == 0:
            arguments.append(call[start:i].strip())
            start = i + 1
        i += 1
    return arguments


def table_fields(value: str) -> set[str]:
    value = value.lstrip()
    if not value.startswith("{"):
        return set()
    fields: set[str] = set()
    depth = 0
    quote = ""
    escaped = False
    i = 0
    while i < len(value):
        char = value[i]
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = ""
        elif char in "\"'":
            quote = char
        elif value.startswith("--", i):
            newline = value.find("\n", i + 2)
            i = len(value) if newline < 0 else newline
            continue
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                break
        elif depth == 1 and (char.isalpha() or char == "_"):
            end = i + 1
            while end < len(value) and (value[end].isalnum() or value[end] == "_"):
                end += 1
            equals = end
            while equals < len(value) and value[equals].isspace():
                equals += 1
            if equals < len(value) and value[equals] == "=":
                fields.add(value[i:end])
            i = end - 1
        i += 1
    return fields


def lua_request_fields(text: str, call_start: int, call: str) -> set[str]:
    arguments = split_call_arguments(call)
    if len(arguments) < 2:
        return set()
    fields = table_fields(arguments[1])
    variable = re.fullmatch(r"([A-Za-z_]\w*)", arguments[1])
    if fields or variable is None or arguments[1] == "nil":
        return fields
    scope = text[text.rfind("\nfunction", 0, call_start) + 1 : call_start]
    name = variable.group(1)
    fields.update(re.findall(rf"\b{re.escape(name)}\.([A-Za-z_]\w*)\s*=", scope))
    assignments = list(re.finditer(rf"\b{re.escape(name)}\s*=\s*(\{{.*?\}})", scope, re.S))
    if assignments:
        fields.update(table_fields(assignments[-1].group(1)))
    return fields


def consumed_fields(text: str, arg: str) -> set[str]:
    return set(re.findall(rf"\b{re.escape(arg)}\.([A-Z][A-Za-z0-9_]*)", text)) if arg else set()


def feature_name(callsites: set[str]) -> str:
    features = set()
    for callsite in callsites:
        name = Path(callsite.rsplit(":", 1)[0]).stem.removeprefix("X")
        for suffix in ("Manager", "Agency", "Control", "Model"):
            if name.endswith(suffix):
                name = name.removesuffix(suffix)
                break
        features.add(name)
    return ",".join(sorted(features))


def parse_lua(root: Path) -> tuple[
    dict[str, set[str]], dict[str, set[str]], dict[str, set[str]], dict[str, set[str]]
]:
    request_calls: dict[str, set[str]] = defaultdict(set)
    request_fields: dict[str, set[str]] = defaultdict(set)
    response_fields: dict[str, set[str]] = defaultdict(set)
    push_consumers: dict[str, set[str]] = defaultdict(set)
    for path in root.rglob("*.lua"):
        text = path.read_text(encoding="utf-8", errors="ignore")
        constants = lua_constants(text)
        relative = path.relative_to(root).as_posix()
        for match in CALL_RE.finditer(text):
            call = text[match.start() : closing_paren(text, match.end() - 1)]
            name = first_argument(call, constants)
            if name is None:
                continue
            request_calls[name].add(f"{relative}:{text.count(chr(10), 0, match.start()) + 1}")
            request_fields[name].update(lua_request_fields(text, match.start(), call))
            for callback in re.finditer(r"function\s*\(\s*([A-Za-z_]\w*)", call):
                response_fields[name].update(consumed_fields(call[callback.end() :], callback.group(1)))

        rpc_matches = list(RPC_RE.finditer(text))
        for index, match in enumerate(rpc_matches):
            end = rpc_matches[index + 1].start() if index + 1 < len(rpc_matches) else len(text)
            push_consumers[match["name"]].update(consumed_fields(text[match.end() : end], match["arg"]))

        for match in ADD_RPC_RE.finditer(text):
            function = re.search(
                rf"function\s+[A-Za-z_]\w*:{re.escape(match['method'])}\s*\(\s*([A-Za-z_]\w*)\s*\)", text
            )
            fields: set[str] = set()
            if function:
                next_function = re.search(r"\nfunction\s", text[function.end() :])
                end = function.end() + next_function.start() if next_function else len(text)
                fields = consumed_fields(text[function.end() : end], function.group(1))
            push_consumers[match["name"]].update(fields)
    return request_calls, request_fields, response_fields, push_consumers


def parse_handlers(root: Path) -> tuple[set[str], set[str]]:
    handlers: set[str] = set()
    pushes: set[str] = set()
    for path in root.rglob("*.cs"):
        text = path.read_text(encoding="utf-8", errors="ignore")
        handlers.update(re.findall(r"RequestPacketHandler\(\"([^\"]+)\"\)", text))
        pushes.update(re.findall(r"SendPush\(\s*\"([^\"]+)\"", text))
        pushes.update(re.findall(r"SendPush\(\s*new\s+([A-Za-z_]\w+)", text))
    return handlers, pushes


def parse_observed(
    paths: Iterable[Path],
) -> tuple[Counter[tuple[str, str]], dict[str, set[str]], dict[str, set[str]]]:
    observed: Counter[tuple[str, str]] = Counter()
    request_fields: dict[str, set[str]] = defaultdict(set)
    request_shapes: dict[str, set[str]] = defaultdict(set)
    files = sorted({
        candidate
        for path in paths
        for candidate in (path.rglob("*summary*.jsonl") if path.is_dir() else [path])
    })
    for path in files:
        with path.open(encoding="utf-8") as handle:
            for line_number, line in enumerate(handle, 1):
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise ValueError(f"{path}:{line_number}: invalid JSON: {exc.msg}") from exc
                if isinstance(row.get("name"), str) and isinstance(row.get("packet_type_name"), str):
                    observed[row["packet_type_name"], row["name"]] += 1
                    if row["packet_type_name"] != "Request":
                        continue
                    summary = row.get("payload_summary")
                    if isinstance(summary, dict):
                        kind = summary.get("kind")
                        if kind == "map":
                            request_shapes[row["name"]].add("map")
                        elif kind == "list":
                            request_shapes[row["name"]].add(f"list[{summary.get('count', '?')}]")
                        keys = summary.get("keys")
                        if isinstance(keys, list):
                            request_fields[row["name"]].update(key for key in keys if isinstance(key, str))
                    elif "payload_len" in row:
                        request_shapes[row["name"]].add("nil" if summary is None else type(summary).__name__)
    return observed, request_fields, request_shapes


def priority(name: str, status: str, count: int, has_lua: bool) -> str:
    if count and status == "missing":
        return "0-observed-missing"
    if name in BOOTSTRAP and status == "missing":
        return "1-bootstrap-missing"
    if count:
        return "2-observed-covered"
    if status == "missing" and has_lua:
        return "3-lua-missing"
    return "4-unobserved"


def rows(
    schemas: dict[str, list[str]],
    calls: dict[str, set[str]],
    lua_fields: dict[str, set[str]],
    responses: dict[str, set[str]],
    consumers: dict[str, set[str]],
    handlers: set[str],
    emitted_pushes: set[str],
    observed: Counter[tuple[str, str]],
    observed_fields: dict[str, set[str]],
    observed_shapes: dict[str, set[str]],
) -> list[list[object]]:
    output: list[list[object]] = []
    requests = set(calls) | handlers | {name for name in schemas if name.endswith("Request")} | {
        name for kind, name in observed if kind == "Request"
    }
    for name in requests:
        status = "handled" if name in handlers else "missing"
        count = observed["Request", name]
        response_name = name.removesuffix("Request") + "Response"
        inferred_fields = lua_fields.get(name, set()) | observed_fields.get(name, set())
        field_sources = ",".join(source for source, present in (
            ("lua", name in lua_fields), ("capture", name in observed_fields)
        ) if present)
        output.append([
            priority(name, status, count, bool(calls.get(name))), "request", name, feature_name(calls.get(name, set())),
            ",".join(sorted(observed_shapes.get(name, set()))), ",".join(schemas.get(name, [])),
            ",".join(sorted(inferred_fields)), field_sources, ",".join(sorted(responses.get(name, set()))),
            ";".join(sorted(calls.get(name, set()))), status, count, response_name if response_name in schemas else "",
        ])

    pushes = set(consumers) | {name for name in schemas if name.startswith("Notify")} | {
        name for kind, name in observed if kind == "Push"
    }
    for name in pushes:
        status = "emitted-reference" if name in emitted_pushes else "unknown"
        count = observed["Push", name]
        output.append([
            "5-observed-push" if count else "6-unobserved-push",
            "push", name, feature_name(set()), "", ",".join(schemas.get(name, [])), "", "",
            ",".join(sorted(consumers.get(name, set()))), "", status, count, "",
        ])
    return sorted(output, key=lambda row: (str(row[0]), str(row[1]), str(row[2])))


REPORT_HEADER = [
    "priority", "kind", "name", "feature", "request_shapes", "schema_fields", "request_fields",
    "request_field_sources", "consumed_fields", "lua_callsites", "ascnet_status", "observed", "response",
]


def cluster_rows(report: list[list[object]]) -> list[list[object]]:
    indexes = {name: index for index, name in enumerate(REPORT_HEADER)}
    clusters: dict[str, list[list[object]]] = defaultdict(list)
    for row in report:
        if row[indexes["priority"]] == "0-observed-missing":
            clusters[str(row[indexes["feature"]] or "Unclassified")].append(row)
    return sorted([
        [
            feature,
            len(items),
            sum(int(item[indexes["observed"]]) for item in items),
            ",".join(sorted(str(item[indexes["name"]]) for item in items)),
        ]
        for feature, items in clusters.items()
    ], key=lambda row: (-int(row[2]), str(row[0])))


def feature_rows(report: list[list[object]]) -> list[list[object]]:
    indexes = {name: index for index, name in enumerate(REPORT_HEADER)}
    features: dict[str, list[list[object]]] = defaultdict(list)
    for row in report:
        if row[indexes["kind"]] == "request":
            features[str(row[indexes["feature"]] or "Unclassified")].append(row)
    return sorted([
        [
            feature,
            len(items),
            sum(item[indexes["ascnet_status"]] == "handled" for item in items),
            sum(item[indexes["ascnet_status"]] == "missing" for item in items),
            sum(int(item[indexes["observed"]]) > 0 for item in items),
            sum(
                int(item[indexes["observed"]]) > 0 and item[indexes["ascnet_status"]] == "missing"
                for item in items
            ),
            sum(int(item[indexes["observed"]]) for item in items),
            ",".join(sorted(str(item[indexes["name"]]) for item in items)),
        ]
        for feature, items in features.items()
    ], key=lambda row: (-int(row[3]), str(row[0])))


def write_tsv(path: Path | None, header: list[str], data: list[list[object]]) -> None:
    handle = path.open("w", encoding="utf-8", newline="") if path else sys.stdout
    try:
        writer = csv.writer(handle, delimiter="\t", lineterminator="\n")
        writer.writerow(header)
        writer.writerows(data)
    finally:
        if path:
            handle.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--metadata", type=Path, default=DEFAULT_METADATA)
    parser.add_argument("--lua-root", type=Path, default=DEFAULT_LUA)
    parser.add_argument("--handlers", type=Path, default=REPO / "AscNet.GameServer")
    parser.add_argument("--summary", type=Path, action="append", default=[], help="Decoded JSONL summary or directory; repeatable")
    parser.add_argument("--output", type=Path, help="TSV output; defaults to stdout")
    parser.add_argument("--clusters-output", type=Path, help="Observed missing requests grouped by Lua feature")
    parser.add_argument("--features-output", type=Path, help="All request coverage grouped by Lua feature")
    args = parser.parse_args()

    for path in (args.metadata, args.lua_root, args.handlers):
        if not path.exists():
            parser.error(f"not found: {path}")

    calls, lua_fields, responses, consumers = parse_lua(args.lua_root)
    handlers, emitted_pushes = parse_handlers(args.handlers)
    observed, observed_fields, observed_shapes = parse_observed(args.summary)
    report = rows(
        parse_metadata(args.metadata), calls, lua_fields, responses, consumers,
        handlers, emitted_pushes, observed, observed_fields, observed_shapes,
    )
    write_tsv(args.output, REPORT_HEADER, report)
    if args.clusters_output:
        write_tsv(args.clusters_output, ["feature", "missing_requests", "observed", "requests"], cluster_rows(report))
    if args.features_output:
        write_tsv(
            args.features_output,
            ["feature", "requests", "handled", "missing", "observed_requests", "observed_missing", "observed", "request_names"],
            feature_rows(report),
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
