"""Project the retail 4.9 EN TRPG (Fake Ascension, main-line chapter 13) tables onto server TSVs.

Decodes the raw `.tab` binaries (several have no JSON sidecar, e.g. TRPGSyncStage) with
scan_client_upgrade's exact decoder and keeps only the structural columns the server reads
(no display text, icons or prefabs).

    import_trpg_4_9.py            print row counts
    import_trpg_4_9.py --apply    write Resources/table/share/trpg/**.tsv and the manifest entries
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import sys
from pathlib import Path

from import_client_tables import render

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / "PGR_DATA/en-4.9/bytes"
TARGET = ROOT / "Resources/table"
MANIFEST = TARGET / "manifest.json"

# table path (relative to share/trpg) -> structural columns; "[]" marks an indexed list column.
TABLES = {
    "TRPGTargetLink": ["Id", "TargetId[]", "PreTarget[]", "TargetMissionType"],
    "TRPGTarget": ["Id", "Type", "StageType", "Params[]"],
    "TRPGReward": ["Id", "Condition[]", "RewardId"],
    "TRPGMemoirStory": ["Id", "UnlockItemId", "UnlockItemCount"],
    "TRPGSyncStage": ["Id", "SyncTargetIds[]"],
    "secondmain/TRPGSecondMainStage": ["Id", "StageType", "StageId", "TRPGRewardId", "Condition[]"],
    "TRPGSyncTarget": ["Id", "SyncStageIds[]"],
    "TRPGRole": ["Id", "UnlockTarget", "InitAttribute[]", "TalentGroupId"],
    "TRPGRoleTalent": ["Id", "ResetTalentItemId", "ResetTalentItemCount"],
    "TRPGRoleTalentGroup": ["GroupId", "Id", "PreId", "CostPoint", "EventId"],
    "TRPGLevel": ["Id", "UpExp", "TalentPoint"],
    "TRPGItem": ["Id", "ItemType", "Capacity", "EffectType", "Params[]"],
    "TRPGBuff": ["Id", "EffectType", "Type", "Params[]"],
    "examine/TRPGExamine": ["Id", "Endurance", "ActionId[]", "PunishId[]", "PunishWeight[]"],
    "examine/TRPGExamineAction": ["Id", "Type", "Round", "ItemId", "NeedValue", "ResetCostId", "ResetCostCount"],
    "examine/TRPGExaminePunish": ["Id", "Type", "Params[]"],
    "TRPGShop": ["Id", "OpenCondition", "ShopItemId[]", "ShopItemCount[]", "Condition"],
    "TRPGShopItem": ["Id", "ResetType", "Condition", "RewardId", "ConsumeId", "ConsumeCount"],
    "boss/TRPGBoss": ["Id", "TimeId", "StageId", "ChallengeCount"],
    "boss/TRPGBossPhasesReward": ["Id", "Percent", "RewardId"],
    "TRPGMaze": ["Id", "StartLayerId", "LayerId[]"],
    "TRPGMazeLayer": ["Id", "StartNodeId", "StartCardIndex", "MapId"],
    "TRPGMazeCard": ["Id", "Disposeable", "SingleDisposeable", "RecordGroupId", "Type", "Param[]", "ConvertCardId"],
    "TRPGMazeMap": ["GroupId", "Id", "CardId[]"],
    "TRPGSecondArea": ["Id", "Type", "MazeId", "Condition"],
    "TRPGThirdArea": ["Id", "FuncGroup[]", "Condition"],
    "TRPGFunctionGroup": ["Id", "FunctionId[]", "Condition"],
}


def decoder():
    sys.path.insert(0, str(Path(__file__).parent))
    spec = importlib.util.spec_from_file_location("scan_client_upgrade", Path(__file__).parent / "scan_client_upgrade.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.ExactTable


# One TSV built from several source tables: name -> (directory, stems); each row gains a GroupId column (the stem).
MERGED = {
    "TRPGRoleTalentGroup": ("roletalentgroup", ["1", "2", "3", "4"]),
    # One map per maze layer (file stem = TRPGMazeLayer.MapId); Id is the node id inside the map.
    "TRPGMazeMap": ("mazemap", sorted(p.stem for p in (SOURCE / "share/trpg/mazemap").glob("*.tab"))),
}


def decode(exact, name):
    if name in MERGED:
        directory, stems = MERGED[name]
        return [dict(row, GroupId=int(stem)) for stem in stems for row in decode(exact, f"{directory}/{stem}")]
    raw = (SOURCE / "share/trpg" / f"{name}.tab").read_bytes()
    table = exact(name.rsplit("/", 1)[-1] + ".tab", raw)
    return [table.decode_row(i) for i in range(table.row_count)]


def project(rows, columns):
    widths = {c[:-2]: max(len(r.get(c[:-2]) or []) for r in rows) for c in columns if c.endswith("[]")}
    header = []
    for column in columns:
        header += [f"{column[:-2]}[{i}]" for i in range(1, widths[column[:-2]] + 1)] if column.endswith("[]") else [column]
    out = []
    for source in rows:
        row = {}
        for column in columns:
            if column.endswith("[]"):
                values = source.get(column[:-2]) or []
                for i in range(widths[column[:-2]]):
                    row[f"{column[:-2]}[{i + 1}]"] = values[i] if i < len(values) else ""
            else:
                row[column] = source.get(column, 0)
        out.append(row)
    return header, out


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    apply = parser.parse_args().apply
    exact = decoder()
    manifest = json.loads(MANIFEST.read_text())
    tables = manifest["tables"]
    for name, columns in TABLES.items():
        rows = decode(exact, name)
        header, rendered = project(rows, columns)
        text = render(header, rendered)
        path = f"share/trpg/{name}.tsv"
        print(path, len(rows), "rows", len(header), "columns")
        if not apply:
            continue
        (TARGET / path).parent.mkdir(parents=True, exist_ok=True)
        (TARGET / path).write_text(text)
        entry = {
            "columns": len(header),
            "contentSha256": hashlib.sha256(text.encode()).hexdigest(),
            "filter": "structural columns only",
            "naturalKey": "GroupId,Id" if name in MERGED else "Id",
            "path": path,
            "rows": len(rows),
            "source": [f"share/trpg/{MERGED[name][0]}/{stem}.tab" for stem in MERGED[name][1]] if name in MERGED else [f"share/trpg/{name}.tab"],
        }
        # Keep the manifest order: replace in place, else insert after the first existing TRPG entry.
        index = next((i for i, old in enumerate(tables) if old["path"] == path), None)
        if index is None:
            index = 1 + next(i for i, old in enumerate(tables) if old["path"] == "share/trpg/TRPGFunction.tsv")
            tables.insert(index, entry)
        else:
            tables[index] = entry
    if apply:
        MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
