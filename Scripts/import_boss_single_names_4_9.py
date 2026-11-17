#!/usr/bin/env python3
"""Add the client's display-name columns to the server's Pain Cage tables (used by /ppc).

    import_boss_single_names_4_9.py [--client ../PGR_DATA/en-4.9/bytes/share/fuben/bosssingle] [--check]

Joins BossSingleStage.BossName (on StageId) and BossSingleChallengeFeature.Name/Desc (on Id) from the client JSON
onto Resources/table/share/fuben/bosssingle/*.tsv. Every other column and row is kept as is; a row missing from
the client table is an error. Re-running replaces the name columns, so the output only depends on the inputs.
--check reports differences without writing. Prints each input JSON's sha256 for the manifest.

Based on @Larkshinnn's append_boss_single_names.py (PR #71).
"""
import argparse
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TABLES = ROOT / "Resources/table/share/fuben/bosssingle"
JOBS = [("BossSingleStage", "StageId", ["BossName"]),
        ("BossSingleChallengeFeature", "Id", ["Name", "Desc"])]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--client", type=Path, default=ROOT.parent / "PGR_DATA/en-4.9/bytes/share/fuben/bosssingle")
    parser.add_argument("--check", action="store_true", help="report differences, write nothing")
    args = parser.parse_args()
    changed = False
    for table, key, columns in JOBS:
        source = args.client / f"{table}.json"
        raw = source.read_bytes()
        rows = {str(row[key]): row for row in json.loads(raw)}
        target = TABLES / f"{table}.tsv"
        text = target.read_bytes().decode("utf-8")
        trailing = text.endswith("\n")
        lines = text[:-1].split("\n") if trailing else text.split("\n")
        header = lines[0].split("\t")
        keep = [i for i, name in enumerate(header) if name not in columns]
        result = ["\t".join([header[i] for i in keep] + columns)]
        for line in lines[1:]:
            cells = line.split("\t")
            row = rows.get(cells[header.index(key)])
            if row is None:
                raise SystemExit(f"{table}: {key} {cells[header.index(key)]} is not in {source}")
            result.append("\t".join([cells[i] for i in keep] + ["" if row.get(c) is None else str(row[c]) for c in columns]))
        output = ("\n".join(result) + ("\n" if trailing else "")).encode("utf-8")
        same = output == target.read_bytes()
        changed |= not same
        print(f"{table}: client json sha256:{hashlib.sha256(raw).hexdigest()}; "
              f"tsv sha256:{hashlib.sha256(output).hexdigest()}; {'unchanged' if same else 'changed'}")
        if not same and not args.check:
            target.write_bytes(output)
    return 1 if changed and args.check else 0


if __name__ == "__main__":
    sys.exit(main())
