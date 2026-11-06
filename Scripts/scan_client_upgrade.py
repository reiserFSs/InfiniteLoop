#!/usr/bin/env python3
"""Stage exact installed EN binary tables and compare their typed rows to PGR_Data."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
from collections import Counter
import json
from pathlib import Path

import pgr_bundles

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--extract", action="store_true", help="scan all effective indexed table bundles")
    modes.add_argument("--roots", action="store_true", help="recover known bundle-root tables")
    modes.add_argument("--recover", action="store_true", help="recover missing or mismatched bundles")
    modes.add_argument("--discover", action="store_true", help="scan structurally valid omitted root tables")
    modes.add_argument("--repair", action="store_true", help="verify raw hashes and every typed JSON row")
    modes.add_argument("--schema", action="store_true", help="compare columns against historical JSON rows")
    parser.add_argument("--install", type=Path, help="game dir (default: source_install of the native-starter manifest)")
    parser.add_argument("--output", type=Path, help="staging dir (default .runtime/upgrade-4.8/tables); keep 4.8 output intact")
    args = parser.parse_args()


ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT.parent / "tools/decode_binary_table.py"
spec = importlib.util.spec_from_file_location("pgr_table_decoder", TOOL)
dec = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dec)
assets = dec.load_module(dec.ASSET_VIEWER, "pgr_assets_upgrade")
OUT = (args.output if __name__ == "__main__" and args.output else ROOT / ".runtime/upgrade-4.8/tables").resolve()
BASE = ROOT.parent / "PGR_Data/en/bytes"
DOMAINS = ("character", "skill", "enhanceskill", "equip", "fashion", "weaponfashion", "partner", "draw", "item", "robot", "npc", "attrib", "grade", "quality", "headportrait")


class ExactReader(dec.Reader):
    def signed(self) -> int:
        n = self.read_uint()
        if n >= 1 << 32:
            raise ValueError(f"integer exceeds signed 32-bit value: {n}")
        return n - (1 << 32) if n & (1 << 31) else n

    def text(self, column):
        return self.folder.pool_string(self.read_uint()) if self.folder.is_pool_column(column) else self.read_raw_string()

    def fixed(self):
        mantissa = self.read_uint()
        if not mantissa:
            return "0x0000000000000000"
        suffix = self.read_byte()
        exponent = suffix & 0x7F
        if exponent > 32:
            raise ValueError(f"fixed point exponent exceeds 32: {exponent}")
        value = mantissa * ((1 << 32) // 10 ** exponent)
        if suffix & 0x80:
            value = -value
        if abs(value) >= 1 << 63:
            raise ValueError("fixed point overflows signed Q32.32")
        return f"0x{value & ((1 << 64) - 1):016X}"

    def indexed(self, value):
        count = self.read_uint()
        result = {}
        for _ in range(count):
            index = self.signed()
            if index in result:
                raise ValueError(f"duplicate indexed key {index}")
            result[index] = value()
        if not result:
            return []
        if min(result) < 1 or max(result) > 100000:
            return {str(k): v for k, v in result.items()}
        return [result.get(k, "") for k in range(1, max(result) + 1)]

    def read_value(self, type_index):
        column = self.aot_index
        self.aot_index += 1
        if type_index == 1:
            return self.read_byte() != 0
        if type_index == 2:
            return self.text(column)
        if type_index == 3:
            return self.fixed()
        if type_index == 4:
            return [self.text(column) for _ in range(self.read_uint())]
        if type_index == 5:
            return [self.read_byte() != 0 for _ in range(self.read_uint())]
        if type_index == 6:
            return [self.signed() for _ in range(self.read_uint())]
        if type_index == 7:
            return [self.signed() / 10000 for _ in range(self.read_uint())]
        if type_index == 8:
            return [self.fixed() for _ in range(self.read_uint())]
        if type_index == 9:
            return {self.text(column): self.text(column) for _ in range(self.read_uint())}
        if type_index == 10:
            return self.indexed(self.signed)
        if type_index == 11:
            return self.indexed(lambda: self.text(column))
        if type_index == 12:
            return {self.text(column): self.signed() for _ in range(self.read_uint())}
        if type_index == 13:
            return self.indexed(lambda: self.signed() / 10000)
        if type_index == 14:
            return self.signed()
        if type_index == 15:
            return self.signed() / 10000
        if type_index == 17:
            return {axis: self.fixed() for axis in ("x", "y", "z")}
        if type_index == 20:
            return [{axis: self.fixed() for axis in ("x", "y", "z")} for _ in range(self.read_uint())]
        if type_index == 23:
            return self.read_byte()
        if type_index == 24:
            return [self.read_byte() for _ in range(self.read_uint())]
        raise ValueError(f"unknown field type {type_index}")


class ExactTable(dec.BinaryTable):
    def row_reader(self, index):
        reader = super().row_reader(index)
        return ExactReader(reader.data, self)

    def pool_string(self, index):
        if index < 0 or index >= len(self.pool_offsets):
            raise ValueError(f"string-pool index {index} outside {len(self.pool_offsets)} strings")
        return super().pool_string(index)

    def decode_row(self, index):
        reader = self.row_reader(index)
        result = {}
        for col in self.columns:
            result[col["name"]] = reader.read_value(col["type"])
        if reader.i != len(reader.data):
            raise ValueError(f"row {index} has {len(reader.data) - reader.i} trailing bytes")
        return result


def dump(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def comparable(row, fixed):
    result = {}
    for name, value in row.items():
        if name in fixed and isinstance(value, str) and value.startswith("0x") and len(value) == 18:
            integer = int(value, 16)
            if integer >= 1 << 63:
                integer -= 1 << 64
            value = round(integer / (1 << 32), 8)
        if value not in (None, "", 0, False, [], {}):
            result[name] = value
    return result


def row_delta(old, new, key, columns):
    if not key or any(key not in r for r in new):
        return {"error": f"missing primary key {key}"}
    # PGR_Data omits zero-valued fields; primary-key zero is encoded in the
    # binary header even when the first historical JSON row omits Id.
    previous = {str(r.get(key, 0)): r for r in old}
    current = {str(r[key]): r for r in new}
    if len(previous) != len(old) or len(current) != len(new):
        return {"error": f"duplicate primary key {key}"}
    fixed = {col["name"] for col in columns if col["type"] == 3}
    return {"added": sorted(current.keys() - previous.keys()),
            "removed": sorted(previous.keys() - current.keys()),
            "changed": sorted(k for k in current.keys() & previous.keys()
                              if comparable(current[k], fixed) != comparable(previous[k], fixed))}


def repair():
    inventory = json.loads((OUT / "manifest.json").read_text())
    deltas = json.loads((OUT / "delta.json").read_text())
    failures = json.loads((OUT / "errors.json").read_text())
    baseline = {r["Name"].lower(): r["Sha1"] for r in json.loads((BASE / "Sha1.json").read_text())}
    for sidecar in OUT.rglob("*.provenance.json"):
        raw_path = sidecar.with_name(sidecar.name.removesuffix(".provenance.json") + ".tab")
        if not raw_path.is_file():
            raise FileNotFoundError(raw_path)
        key = str(raw_path.relative_to(OUT)).lower()
        item = {**inventory.get(key, {}), **json.loads(sidecar.read_text())}
        if hashlib.sha1(raw_path.read_bytes()).hexdigest() != item["sha1"]:
            raise ValueError(f"staged raw table changed: {raw_path}")
        item["baseline_sha1"] = baseline.get(key)
        inventory[key] = item
        failures.pop(key, None)
        table = ExactTable(raw_path.name, raw_path.read_bytes())
        rows = table.decode_rows()
        old_path = BASE / f"{raw_path.relative_to(OUT).parent}/{raw_path.stem}.json"
        if old_path.is_file():
            previous = json.loads(old_path.read_text())
            delta = row_delta(previous, rows, table.primary_key, table.columns)
            delta["schema_added"] = sorted(set(rows[0] if rows else {}) - set(previous[0] if previous else {}))
            delta["schema_removed"] = sorted(set(previous[0] if previous else {}) - set(rows[0] if rows else {}))
        else:
            delta = {"baseline_unavailable": True, "rows": len(rows)}
        deltas[key] = delta
    dump(OUT / "manifest.json", inventory)
    dump(OUT / "delta.json", deltas)
    dump(OUT / "errors.json", failures)
    items = sorted(inventory.items(), key=lambda pair: (not any(domain in pair[0] for domain in DOMAINS), pair[0]))
    restored = []
    audited = 0
    for number, (key, item) in enumerate(items, 1):
        parent = OUT / key.rsplit("/", 1)[0]
        raw_name = key.rsplit("/", 1)[-1]
        raw_path = next((p for p in parent.iterdir() if p.name.lower() == raw_name), None)
        if raw_path is None:
            failures[key] = "staged raw asset missing"
            continue
        try:
            content = raw_path.read_bytes()
            if hashlib.sha1(content).hexdigest() != item["sha1"] or len(content) != item["size"]:
                raise ValueError("staged raw asset hash/size differs from verified provenance")
            table = ExactTable(raw_path.name, content)
            rows = table.decode_rows()
            staged = raw_path.with_name(raw_path.name.removesuffix(".tab") + ".json")
            try:
                same = staged.is_file() and json.loads(staged.read_text()) == rows
            except (ValueError, UnicodeError):
                same = False
            if not same:
                dump(staged, rows)
                restored.append(key)
            audited += 1
            item["schema"] = table.summary()
            item.pop("decode_error", None)
            failures.pop(key, None)
            old_path = BASE / f"{key.rsplit('/', 1)[0]}/{raw_path.name.removesuffix('.tab')}.json"
            if old_path.is_file():
                previous = json.loads(old_path.read_text())
                delta = row_delta(previous, rows, table.primary_key, table.columns)
                delta["schema_added"] = sorted(set(rows[0] if rows else {}) - set(previous[0] if previous else {}))
                delta["schema_removed"] = sorted(set(previous[0] if previous else {}) - set(rows[0] if rows else {}))
            else:
                delta = {"baseline_unavailable": True, "rows": len(rows)}
            deltas[key] = delta
        except Exception as exc:
            item["decode_error"] = str(exc)
            failures[key] = str(exc)
        if number % 250 == 0:
            dump(OUT / "manifest.json", inventory)
            dump(OUT / "delta.json", deltas)
            dump(OUT / "errors.json", failures)
            print(f"Repaired {number}/{len(inventory)}: {len(failures)} errors", flush=True)
    dump(OUT / "manifest.json", inventory)
    dump(OUT / "delta.json", deltas)
    dump(OUT / "errors.json", failures)
    index_delta = json.loads((OUT / "index-delta.json").read_text())
    index_delta["baseline_missing"] = sorted(baseline.keys() - inventory.keys())
    index_delta["installed_new"] = sorted(inventory.keys() - baseline.keys())
    dump(OUT / "index-delta.json", index_delta)
    dump(OUT / "report.json", {
        "baseline": str(BASE),
        "effective_index_precedence": "document then resource only when absent",
        "table_count": len(inventory),
        "audited_typed_tables": audited,
        "restored_staged_json": restored,
        "previously_restored": {"client/miniactivity/fangkuai/fangkuaicharacter.tab": "a6dac8d7e12bcd028159e1a3a212766b79dc18b7"},
        "typed_count": sum("schema" in item and "decode_error" not in item for item in inventory.values()),
        "raw_only_count": sum("schema" not in item for item in inventory.values()),
        "raw_changed_against_baseline_sha1": sum(bool(item.get("baseline_sha1")) and
                                                 item["sha1"] != item["baseline_sha1"] for item in inventory.values()),
        "raw_unchanged_against_baseline_sha1": sum(bool(item.get("baseline_sha1")) and
                                                   item["sha1"] == item["baseline_sha1"] for item in inventory.values()),
        "row_delta_tables": len(deltas),
        "baseline_table_missing_count": len(index_delta["baseline_missing"]),
        "installed_table_absent_baseline_sha1_count": len(index_delta["installed_new"]),
        "error_categories": dict(Counter(v.split(":")[0] for v in failures.values())),
        "fidelity_errors": failures,
        "comparison_note": "Baseline old JSON omits defaults; changed-row comparison treats absent/default values alike and rounds fixed Q32.32 to eight decimals. Raw SHA1 compares exact bytes only for baseline Sha1.json entries.",
        "index_note": "Index-delta compares installed document vs installed resource, not a historic index; 4.7 baseline table hashes/JSON are the historical table delta.",
    })

def refresh_schema_deltas():
    inventory = json.loads((OUT / "manifest.json").read_text())
    deltas = json.loads((OUT / "delta.json").read_text())
    checked = 0
    for key, item in inventory.items():
        delta = deltas.get(key)
        if not delta or "schema_added" not in delta or "schema" not in item:
            continue
        raw_path = OUT / key
        baseline_path = BASE / f"{key.rsplit('/', 1)[0]}/{raw_path.name.removesuffix('.tab')}.json"
        if not baseline_path.is_file():
            continue
        old_fields = set().union(*(row.keys() for row in json.loads(baseline_path.read_text())))
        new_fields = {column["name"] for column in item["schema"]["columns"]}
        delta["schema_added"] = sorted(new_fields - old_fields)
        delta["schema_removed"] = sorted(old_fields - new_fields)
        checked += 1
    dump(OUT / "delta.json", deltas)
    print(f"Compared schemas against all historical row keys in {checked} tables")


def run(roots_only=False, recover_only=False, discover_only=False):
    install = args.install or Path(json.loads(dec.DEFAULT_MANIFEST.read_text())["source_install"])
    resolver = pgr_bundles.Resolver(install)
    indexes = {scope: resolver.indexes.get(scope, {}) for scope in ("resource", "document")}
    effective = {**indexes["resource"], **indexes["document"]}
    baseline = {r["Name"].lower(): r["Sha1"] for r in json.loads((BASE / "Sha1.json").read_text())}
    inventory = json.loads((OUT / "manifest.json").read_text()) if roots_only or recover_only or discover_only else {}
    failures = json.loads((OUT / "errors.json").read_text()) if roots_only or recover_only or discover_only else {}
    deltas = json.loads((OUT / "delta.json").read_text()) if roots_only or recover_only or discover_only else {}
    candidates = resolver.names("assets/temp/bytes/", ".ab")  # index names + names found by scanning bundle contents
    if roots_only:
        candidates = [k for k in candidates if
                      f"{k.removeprefix('assets/temp/bytes/').removesuffix('.ab')}/{Path(k).stem}.tab".lower() not in inventory]
        candidates.sort(key=lambda k: (not any(domain in k for domain in DOMAINS), k))
    if recover_only:
        candidates = [k for k in candidates if k in failures]
    for number, logical in enumerate(candidates, 1):
        rel = logical.removeprefix("assets/temp/bytes/").removesuffix(".ab")
        try:
            path = resolver.resolve(logical)  # real index (4.8) or bundle contents (4.9); scope-aware, document first
            raw = path.read_bytes()
            scope = path.parent.parent.name
            entry = resolver.entry(logical) or resolver.file_entry(path)  # 4.8 logical index entry / 4.9 file table entry
            if entry is None:  # not listed by any index: SHA1 cannot be checked
                entry = [path.name, hashlib.sha1(raw).hexdigest(), len(raw)]
            elif len(raw) != entry[2] or hashlib.sha1(raw).hexdigest().lower() != str(entry[1]).lower():
                raise ValueError(f"no bundle with effective index SHA1/size: {logical} {entry[1:]} ({path})")
            source_url = None
            payload = pgr_bundles.payload(raw)
            failures.pop(logical, None)
            # Accept serialized paths, baseline-known names, or independently
            # validated BinaryTable structures; heuristic strings alone are insufficient.
            named_paths = {x["name"].lower().removeprefix("assets/temp/bytes/").removesuffix(".bytes")
                           for x in dec.iter_text_assets(assets, payload)
                           if x["name"].lower().startswith("assets/temp/bytes/") and x["name"].lower().endswith(".bytes")}
            seen = set()
            for asset in dec.iter_text_assets(assets, payload):
                name = asset["name"]
                key = f"{rel}/{name}".lower()
                if discover_only and key in inventory:
                    continue
                known = key in baseline or (BASE / f"{rel}/{name.removesuffix('.tab')}.json").is_file()
                if key not in named_paths and not known and name.lower() not in (Path(rel).name.lower(), Path(rel).name.lower() + ".tab"):
                    if not discover_only or not name.endswith(".tab") or not name.removesuffix(".tab").replace("_", "").isalnum():
                        continue
                    try:
                        candidate = ExactTable(name, asset["data"])
                        if candidate.pool_start > len(asset["data"]) or (
                            candidate.row_ends and (candidate.row_ends[-1] != candidate.content_len or
                                                    any(x > y for x, y in zip(candidate.row_ends, candidate.row_ends[1:])))):
                            continue
                    except Exception:
                        continue
                if key in seen:
                    continue
                seen.add(key)
                source = assets.find_embedded_text_asset(payload, name)
                if source is None or source["content"] != asset["data"]:
                    raise ValueError(f"ambiguous serialized TextAsset {name}")
                raw_table = source["content"]
                target = OUT / f"{rel}/{name}"
                item = {"bundle": logical, "scope": scope, "filename": str(path),
                        "source_url": source_url, "index_sha1": entry[1], "index_size": entry[2],
                        "sha1": hashlib.sha1(raw_table).hexdigest(), "size": len(raw_table),
                        "baseline_sha1": baseline.get(key)}
                inventory[key] = item
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(raw_table)
                if item["sha1"] == item["baseline_sha1"] and not any(domain in key for domain in DOMAINS):
                    continue
                try:
                    table = ExactTable(name, raw_table)
                    rows = table.decode_rows()
                    item["schema"] = table.summary()
                    dump(target.with_name(name.removesuffix(".tab") + ".json"), rows)
                    previous_path = BASE / f"{rel}/{name.removesuffix('.tab')}.json"
                    if previous_path.is_file():
                        previous = json.loads(previous_path.read_text())
                        delta = row_delta(previous, rows, table.primary_key, table.columns)
                        delta["schema_added"] = sorted(set(rows[0] if rows else {}) - set(previous[0] if previous else {}))
                        delta["schema_removed"] = sorted(set(previous[0] if previous else {}) - set(rows[0] if rows else {}))
                    else:
                        delta = {"baseline_unavailable": True, "rows": len(rows)}
                    deltas[key] = delta
                except Exception as exc:
                    item["decode_error"] = str(exc)
                    failures[key] = str(exc)
        except Exception as exc:
            failures[logical] = str(exc)
        if number % 25 == 0:
            dump(OUT / "manifest.json", inventory)
            dump(OUT / "delta.json", deltas)
            dump(OUT / "errors.json", failures)
            print(f"{number}/{len(candidates)} bundles, {len(inventory)} tables, {len(failures)} errors", flush=True)
    dump(OUT / "manifest.json", inventory)
    dump(OUT / "delta.json", deltas)
    dump(OUT / "errors.json", failures)
    tables = {tree: resolver.file_tables(tree) for tree in pgr_bundles.TREES}  # 4.9 physical-file tables
    file_delta = {}
    for tree, per_scope in tables.items():
        doc, res = (per_scope.get(s, {}).get("files", {}) for s in ("document", "resource"))
        if doc or res:
            file_delta[tree] = {"document_entries": len(doc), "resource_entries": len(res),
                                "resource_only": sorted(res.keys() - doc.keys()), "document_only": sorted(doc.keys() - res.keys()),
                                "overrides": sorted(k for k in doc.keys() & res.keys() if doc[k][1:3] != res[k][1:3])}
    dump(OUT / "index-delta.json", {"index_sha1": {f"{scope}/{tree}": hashlib.sha1(p.read_bytes()).hexdigest()
                                                    for tree in pgr_bundles.TREES for scope in pgr_bundles.SCOPES
                                                    if (p := pgr_bundles._index_path(install, scope, tree))},
                                     "resource_only": sorted(indexes["resource"].keys() - indexes["document"].keys()),
                                     "document_only": sorted(indexes["document"].keys() - indexes["resource"].keys()),
                                     "overrides": sorted(k for k in indexes["document"].keys() & indexes["resource"].keys()
                                                         if indexes["document"][k][1:] != indexes["resource"][k][1:]),
                                     "file_tables": file_delta,
                                     "baseline_missing": sorted(baseline.keys() - inventory.keys()),
                                     "installed_new": sorted(inventory.keys() - baseline.keys())})
    print(f"Complete: {len(inventory)} tables; {len(deltas)} row deltas; {len(failures)} errors")


if __name__ == "__main__":
    if args.repair:
        repair()
    elif args.schema:
        refresh_schema_deltas()
    else:
        run(roots_only=args.roots, recover_only=args.recover, discover_only=args.discover)
