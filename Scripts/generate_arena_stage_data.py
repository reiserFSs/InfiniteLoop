#!/usr/bin/env python3
"""Generate normalized Arena enemy-wave data from decoded retail captures."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any
import csv



def canonical(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"))


def intern(value: Any, values: list[Any], indexes: dict[str, int]) -> int:
    key = canonical(value)
    if key not in indexes:
        indexes[key] = len(values)
        values.append(value)
    return indexes[key]

def captured(capture_dir: Path, suffix: str, stage_id: int | None) -> Any:
    matches = sorted(capture_dir.glob(f"*-{suffix}.json"))
    candidates: list[Any] = []
    for match in matches:
        value = json.loads(match.read_text())
        payload = value.get("PreFightData") or value.get("FightData") or value.get("Result") or value.get("Settle")
        if payload is not None and (stage_id is None or payload.get("StageId") == stage_id):
            candidates.append(value)
    if len(candidates) != 1:
        raise ValueError(f"{capture_dir}: expected one stage-matching *-{suffix}.json, found {len(candidates)}; pass --stage-id")
    return candidates[0]


def area_stage_profile(area_id: int, stage_id: int) -> tuple[int, str]:
    table_path = Path(__file__).resolve().parent.parent / "Resources/table/share/fuben/arena/AreaStage.tsv"
    with table_path.open(newline="") as stream:
        rows = [
            row for row in csv.DictReader(stream, delimiter="\t")
            if int(row["Id"]) == area_id
            and stage_id in (int(row["StageId[1]"]), int(row["StageId[2]"]))
        ]
    if len(rows) != 1:
        raise ValueError(f"{table_path}: expected one AreaStage for area {area_id}, stage {stage_id}, found {len(rows)}")
    return int(rows[0]["MarkId"]), rows[0]["Desc"]


def generate(capture_dir: Path, stage_id: int | None = None) -> dict[str, Any]:
    prefight_request = captured(capture_dir, "PreFightRequest", stage_id)
    prefight = captured(capture_dir, "PreFightResponse", stage_id)["FightData"]
    settle_request = captured(capture_dir, "FightSettleRequest", stage_id)["Result"]
    settle = captured(capture_dir, "FightSettleResponse", stage_id)["Settle"]
    request = prefight_request["PreFightData"]
    if request["StageId"] != prefight["StageId"] or settle_request["StageId"] != prefight["StageId"] or settle["StageId"] != prefight["StageId"]:
        raise ValueError("capture stage IDs disagree")
    for key in ("FightId", "Uuid"):
        if key in prefight and key in settle_request and prefight[key] != settle_request[key]:
            raise ValueError(f"capture {key} values disagree")
    captured_groups = prefight.get("NpcGroupList", [])
    if not captured_groups or any(not group.get("NpcList") for group in captured_groups):
        raise ValueError("captured Arena waves must contain NPCs")
    progress = settle_request.get("StringToIntRecord", {}).get("NpcGroup")
    wave_times = settle_request.get("StringToListIntRecord", {}).get("WaveCostTimes")
    if progress is not None and wave_times is not None:
        if len(wave_times) != progress - 1:
            raise ValueError("captured wave timing count disagrees with NpcGroup progress")
        completed_enemy_count = sum(len(group["NpcList"]) for group in captured_groups[:len(wave_times)])
        if settle_request.get("DeathTotalEnemy") != completed_enemy_count:
            raise ValueError("captured enemy deaths disagree with completed wave NPC counts")


    npc_definitions: list[Any] = []
    npc_indexes: dict[str, int] = {}
    group_definitions: list[Any] = []
    group_indexes: dict[str, int] = {}
    waves: list[int] = []
    for captured_group in captured_groups:
        npc_refs = [intern(npc, npc_definitions, npc_indexes) for npc in captured_group["NpcList"]]
        waves.append(intern({"NpcRefs": npc_refs}, group_definitions, group_indexes))
    if any(ref < 0 or ref >= len(group_definitions) for ref in waves):
        raise ValueError("generated Arena wave reference is invalid")

    mark_id, archetype = area_stage_profile(request["SelectAreaId"], prefight["StageId"])
    return {
        "SchemaVersion": 2,
        "NpcDefinitions": npc_definitions,
        "GroupDefinitions": group_definitions,
        "Stages": [{
            "StageId": prefight["StageId"],
            "AreaId": request["SelectAreaId"],
            "MarkId": mark_id,
            "Archetype": archetype,
            "ReusableArchetype": True,
            "NpcGroupRefs": waves,
            "PassTimeLimit": prefight["PassTimeLimit"],
            "ReviseId": prefight["ReviseId"],
            "Restartable": prefight["Restartable"],
            "FightCheckType": prefight.get("FightCheckType", 0),
            "SegmentFightCheckSecond": prefight.get("SegmentFightCheckSecond", 0),
            "Records": prefight["Records"],
            "StageParams": {key: prefight["StageParams"][key] for key in ("WaveCostTimes", "TimePointScore")},
        }],
    }

def merge(datasets: list[dict[str, Any]]) -> dict[str, Any]:
    merged: dict[str, Any] = {"SchemaVersion": 2, "NpcDefinitions": [], "GroupDefinitions": [], "Stages": []}
    npc_indexes: dict[str, int] = {}
    group_indexes: dict[str, int] = {}
    for dataset in datasets:
        npc_map = {
            index: intern(npc, merged["NpcDefinitions"], npc_indexes)
            for index, npc in enumerate(dataset["NpcDefinitions"])
        }
        group_map: dict[int, int] = {}
        for index, group in enumerate(dataset["GroupDefinitions"]):
            translated = {"NpcRefs": [npc_map[ref] for ref in group["NpcRefs"]]}
            group_map[index] = intern(translated, merged["GroupDefinitions"], group_indexes)
        for stage in dataset["Stages"]:
            translated_stage = dict(stage)
            translated_stage["NpcGroupRefs"] = [group_map[ref] for ref in stage["NpcGroupRefs"]]
            merged["Stages"].append(translated_stage)
    keys = [(stage["AreaId"], stage["StageId"]) for stage in merged["Stages"]]
    if len(keys) != len(set(keys)):
        raise ValueError("capture inputs contain duplicate Arena area/stage keys")
    reusable_profiles = [(stage["MarkId"], stage["Archetype"]) for stage in merged["Stages"] if stage["ReusableArchetype"]]
    if len(reusable_profiles) != len(set(reusable_profiles)):
        raise ValueError("capture inputs contain duplicate reusable Arena MarkId/archetype profiles")
    return merged



def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("capture_dir", type=Path)
    parser.add_argument("--stage-id", type=int, help="stage to select when a capture directory contains multiple runs")
    parser.add_argument("--additional-capture-dir", action="append", type=Path, default=[],
                        help="additional capture run to merge into the normalized dataset")
    parser.add_argument("output", type=Path, nargs="?", default=Path("Resources/Configs/arena_stage_data.json"))
    args = parser.parse_args()
    paths = [args.capture_dir, *args.additional_capture_dir]
    dataset = merge([generate(path, args.stage_id if index == 0 else None) for index, path in enumerate(paths)])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dataset, indent=2) + "\n")


if __name__ == "__main__":
    main()
