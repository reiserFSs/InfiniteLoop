#!/usr/bin/env python3
"""Generate the current Study catalog from decoded installed-client Share tables."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import sys
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[1]
# EN 4.9.1 install tables (staged by scan_client_upgrade.py --extract). CN/KR/JP generate the same
# catalog apart from localized text; TW 4.9 lacks 39 Stage rows and is not a valid source.
DEFAULT_SOURCE = REPO_ROOT.parent / "PGR_DATA" / "en-4.9" / "bytes"
DEFAULT_OUTPUT = REPO_ROOT / "Resources" / "Configs" / "study_compatibility_4.9.1.json"
CLIENT_VERSION = "4.9.1"
SOURCES = {
    "Stage": "share/fuben/Stage.json",
    "StageLevelControl": "share/fuben/StageLevelControl.json",
    "Robot": "share/robot/Robot.json",
    "PracticeChapter": "share/fuben/practice/PracticeChapter.json",
    "PracticeGroup": "share/fuben/practice/PracticeGroup.json",
    "PracticeActivity": "share/fuben/practice/PracticeActivity.json",
    "TeachingActivity": "share/fuben/teaching/TeachingActivity.json",
    "TeachingRobot": "share/fuben/teaching/TeachingRobot.json",
    "EnhanceSkill": "share/character/enhanceskill/EnhanceSkill.json",
    "EnhanceSkillGroup": "share/character/enhanceskill/EnhanceSkillGroup.json",
}


def positive_ids(value: Any) -> list[int]:
    if value is None:
        return []
    if not isinstance(value, list) or any(not isinstance(item, int) for item in value):
        raise ValueError(f"expected an integer array, got {value!r}")
    return [item for item in value if item > 0]


def load_sources(source: Path) -> tuple[dict[str, list[dict[str, Any]]], dict[str, str], dict[str, dict[str, str]]]:
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8"))
    tables: dict[str, list[dict[str, Any]]] = {}
    hashes: dict[str, str] = {}
    provenance: dict[str, dict[str, str]] = {}
    for name, relative_path in SOURCES.items():
        path = source / relative_path
        raw = path.read_bytes()
        root = json.loads(raw)
        if not isinstance(root, list) or any(not isinstance(row, dict) for row in root):
            raise ValueError(f"{path}: expected an array of objects")
        table_key = str(Path(relative_path).with_suffix(".tab")).lower()
        entry = manifest.get(table_key)
        if entry is None:
            entry = json.loads(path.with_suffix(".provenance.json").read_text(encoding="utf-8"))
        table_hash = hashlib.sha1((source / Path(relative_path).with_suffix(".tab")).read_bytes()).hexdigest()
        if table_hash != entry["sha1"]:
            raise ValueError(f"{table_key}: decoded table does not match installed-client manifest")
        tables[name] = root
        hashes[name] = hashlib.sha1(raw).hexdigest()
        provenance[name] = {
            "TableSha1": table_hash, "Bundle": entry["bundle"],
            "IndexSha1": entry["index_sha1"], "Scope": entry["scope"],
        }
    return tables, hashes, provenance


def unique_by(rows: list[dict[str, Any]], field: str, source: str) -> dict[int, dict[str, Any]]:
    result: dict[int, dict[str, Any]] = {}
    for row in rows:
        key = row.get(field)
        if not isinstance(key, int) or key <= 0 or key in result:
            raise ValueError(f"{source}: invalid or duplicate {field} {key!r}")
        result[key] = row
    return result


def build_catalog(tables: dict[str, list[dict[str, Any]]], hashes: dict[str, str], provenance: dict[str, dict[str, str]]) -> dict[str, Any]:
    study_stage_ids: set[int] = set()
    for row in tables["PracticeGroup"]:
        study_stage_ids.update(positive_ids(row.get("StageIds")))
        study_stage_ids.update(positive_ids(row.get("LinkStageIds")))
    for row in tables["PracticeActivity"]:
        stage_id = row.get("StageId")
        if isinstance(stage_id, int) and stage_id > 0:
            study_stage_ids.add(stage_id)
    for row in tables["TeachingActivity"]:
        for field in ("StageId", "ChallengeStage", "LinkStageId"):
            study_stage_ids.update(positive_ids(row.get(field)))

    all_stages = unique_by(tables["Stage"], "StageId", "Stage")
    missing_stages = sorted(study_stage_ids - all_stages.keys())
    if missing_stages:
        raise ValueError(f"Study sources reference missing Stage rows: {missing_stages}")
    stages = [copy.deepcopy(row) for row in tables["Stage"] if row["StageId"] in study_stage_ids]
    # Stage contains links into unrelated progression namespaces. The compatibility
    # catalog is a closed Study projection, so only edges whose endpoints are selected remain.
    for row in stages:
        for field in ("PreStageId", "NextStageId"):
            if field in row:
                row[field] = [stage_id for stage_id in positive_ids(row[field]) if stage_id in study_stage_ids]

    practice_groups = unique_by(tables["PracticeGroup"], "GroupId", "PracticeGroup")
    practice_owned: set[int] = set()
    for chapter in tables["PracticeChapter"]:
        practice_owned.update(positive_ids(chapter.get("StageId")))
        for group_id in positive_ids(chapter.get("Groups")):
            if group_id not in practice_groups:
                raise ValueError(f"PracticeChapter references missing PracticeGroup {group_id}")
            practice_owned.update(positive_ids(practice_groups[group_id].get("StageIds")))
    teaching_owned = {
        stage_id
        for row in tables["TeachingActivity"]
        for field in ("StageId", "ChallengeStage")
        for stage_id in positive_ids(row.get(field))
    }
    if practice_owned & teaching_owned or practice_owned | teaching_owned != study_stage_ids:
        raise ValueError("every Study stage must belong to exactly one progression namespace")

    predecessor_edges = {
        (predecessor, row["StageId"])
        for row in stages
        for predecessor in positive_ids(row.get("PreStageId"))
    }
    successor_edges = {
        (row["StageId"], successor)
        for row in stages
        for successor in positive_ids(row.get("NextStageId"))
    }
    if predecessor_edges != successor_edges:
        raise ValueError("Study PreStageId/NextStageId edges are not reciprocal")

    teaching_robot_by_stage = unique_by(tables["TeachingRobot"], "StageId", "TeachingRobot")
    robot_ids: set[int] = set()
    for stage in stages:
        teaching = teaching_robot_by_stage.get(stage["StageId"])
        configured = positive_ids(teaching.get("RobotId")) if teaching else []
        robot_ids.update(configured or positive_ids(stage.get("RobotId")))
    all_robots = unique_by(tables["Robot"], "Id", "Robot")
    missing_robots = sorted(robot_ids - all_robots.keys())
    if missing_robots:
        raise ValueError(f"Study stages reference missing Robot rows: {missing_robots}")
    robots = [row for row in tables["Robot"] if row["Id"] in robot_ids]
    controls = [row for row in tables["StageLevelControl"] if row.get("StageId") in study_stage_ids]
    if any(row.get("StageId") not in study_stage_ids for row in controls):
        raise ValueError("selected StageLevelControl references a missing Study stage")
    unique_by(controls, "Id", "selected StageLevelControl")
    if any(not isinstance(robot.get("CharacterId"), int) or robot["CharacterId"] <= 0 for robot in robots):
        raise ValueError("selected Robot row is missing a positive CharacterId")

    # The client resolves a premade frame's enhance skills from the same client version as the
    # Robot row, so freeze that version's enhance inputs alongside the selected robots. The
    # projection is closed: every selected character has its authored row and every referenced
    # group is present.
    character_ids = {robot["CharacterId"] for robot in robots}
    all_enhance_skills = unique_by(tables["EnhanceSkill"], "CharacterId", "EnhanceSkill")
    if character_ids - all_enhance_skills.keys():
        raise ValueError(
            f"selected Robots reference missing EnhanceSkill rows: {sorted(character_ids - all_enhance_skills.keys())}")
    enhance_skills = [row for row in tables["EnhanceSkill"] if row["CharacterId"] in character_ids]
    enhance_group_ids = {group_id for row in enhance_skills for group_id in positive_ids(row.get("SkillGroupId"))}
    all_enhance_groups = unique_by(tables["EnhanceSkillGroup"], "Id", "EnhanceSkillGroup")
    if enhance_group_ids - all_enhance_groups.keys():
        raise ValueError(
            f"selected EnhanceSkill rows reference missing EnhanceSkillGroup rows: "
            f"{sorted(enhance_group_ids - all_enhance_groups.keys())}")
    enhance_skill_groups = [row for row in tables["EnhanceSkillGroup"] if row["Id"] in enhance_group_ids]

    counts = {
        "PracticeChapters": len(tables["PracticeChapter"]),
        "PracticeGroups": len(tables["PracticeGroup"]),
        "PracticeActivities": len(tables["PracticeActivity"]),
        "TeachingActivities": len(tables["TeachingActivity"]),
        "TeachingRobots": len(tables["TeachingRobot"]),
        "StudyStages": len(stages),
        "StageLevelControls": len(controls),
        "Robots": len(robots),
        "EnhanceSkills": len(enhance_skills),
        "EnhanceSkillGroups": len(enhance_skill_groups),
    }
    return {
        "ClientVersion": CLIENT_VERSION,
        "SourcePaths": SOURCES,
        "SourceHashes": hashes,
        "SourceProvenance": provenance,
        "ExpectedCounts": counts,
        "PracticeGroups": tables["PracticeGroup"],
        "PracticeChapters": tables["PracticeChapter"],
        "PracticeActivities": tables["PracticeActivity"],
        "TeachingActivities": tables["TeachingActivity"],
        "TeachingRobots": tables["TeachingRobot"],
        "Stages": stages,
        "StageLevelControls": controls,
        "Robots": robots,
        "EnhanceSkills": enhance_skills,
        "EnhanceSkillGroups": enhance_skill_groups,
    }


def render(catalog: dict[str, Any]) -> str:
    return json.dumps(catalog, ensure_ascii=False, separators=(",", ":")) + "\n"


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    tables, hashes, provenance = load_sources(args.source.resolve())
    catalog = build_catalog(tables, hashes, provenance)
    generated = render(catalog)
    if args.check:
        if not args.output.exists() or args.output.read_text(encoding="utf-8") != generated:
            print(f"stale: {args.output}", file=sys.stderr)
            return 1
        print(f"up-to-date: {args.output}")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(generated, encoding="utf-8")
        print(f"generated: {args.output}")
    print(" ".join(f"{name}={count}" for name, count in catalog["ExpectedCounts"].items()))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
