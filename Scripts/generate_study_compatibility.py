#!/usr/bin/env python3
"""Generate the current Study compatibility catalog from authoritative EN tables."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = REPO_ROOT.parent / "PGR_Data"
DEFAULT_OUTPUT = REPO_ROOT / "Resources" / "Configs" / "study_compatibility_4.6.0.json"
SOURCE_REVISION = "bb3c34765c9d9c1c542079d536a17e82b27f3245"
SOURCE_DATE = "2026-07-17"  # Author date of SOURCE_REVISION; never wall-clock time.
CLIENT_VERSION = "4.6.0"
SOURCES = {
    "Stage": "en/bytes/share/fuben/Stage.json",
    "StageLevelControl": "en/bytes/share/fuben/StageLevelControl.json",
    "Robot": "en/bytes/share/robot/Robot.json",
    "PracticeChapter": "en/bytes/share/fuben/practice/PracticeChapter.json",
    "PracticeGroup": "en/bytes/share/fuben/practice/PracticeGroup.json",
    "PracticeActivity": "en/bytes/share/fuben/practice/PracticeActivity.json",
    "TeachingActivity": "en/bytes/share/fuben/teaching/TeachingActivity.json",
    "TeachingRobot": "en/bytes/share/fuben/teaching/TeachingRobot.json",
}

def require_clean_sources(source: Path) -> None:
    result = subprocess.run(
        ["git", "-C", str(source), "diff", "--quiet", "--no-ext-diff", SOURCE_REVISION, "--", *SOURCES.values()],
        check=False,
    )
    if result.returncode == 1:
        raise ValueError(f"{source}: Study inputs differ from pinned revision {SOURCE_REVISION}")
    if result.returncode != 0:
        raise subprocess.CalledProcessError(result.returncode, result.args)


def positive_ids(value: Any) -> list[int]:
    if value is None:
        return []
    if not isinstance(value, list) or any(not isinstance(item, int) for item in value):
        raise ValueError(f"expected an integer array, got {value!r}")
    return [item for item in value if item > 0]


def load_sources(source: Path) -> tuple[dict[str, list[dict[str, Any]]], dict[str, str]]:
    revision = subprocess.run(
        ["git", "-C", str(source), "rev-parse", "HEAD"],
        check=True, capture_output=True, text=True,
    ).stdout.strip()
    if revision != SOURCE_REVISION:
        raise ValueError(f"{source}: expected revision {SOURCE_REVISION}, got {revision}")
    require_clean_sources(source)

    tables: dict[str, list[dict[str, Any]]] = {}
    hashes: dict[str, str] = {}
    for name, relative_path in SOURCES.items():
        path = source / relative_path
        raw = path.read_bytes()
        root = json.loads(raw)
        if not isinstance(root, list) or any(not isinstance(row, dict) for row in root):
            raise ValueError(f"{path}: expected an array of objects")
        tables[name] = root
        hashes[name] = hashlib.sha1(raw).hexdigest()
    return tables, hashes


def unique_by(rows: list[dict[str, Any]], field: str, source: str) -> dict[int, dict[str, Any]]:
    result: dict[int, dict[str, Any]] = {}
    for row in rows:
        key = row.get(field)
        if not isinstance(key, int) or key <= 0 or key in result:
            raise ValueError(f"{source}: invalid or duplicate {field} {key!r}")
        result[key] = row
    return result


def build_catalog(tables: dict[str, list[dict[str, Any]]], hashes: dict[str, str]) -> dict[str, Any]:
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

    counts = {
        "PracticeChapters": len(tables["PracticeChapter"]),
        "PracticeGroups": len(tables["PracticeGroup"]),
        "PracticeActivities": len(tables["PracticeActivity"]),
        "TeachingActivities": len(tables["TeachingActivity"]),
        "TeachingRobots": len(tables["TeachingRobot"]),
        "StudyStages": len(stages),
        "StageLevelControls": len(controls),
        "Robots": len(robots),
    }
    return {
        "ClientVersion": CLIENT_VERSION,
        "GeneratedDate": SOURCE_DATE,
        "SourceRevision": SOURCE_REVISION,
        "SourcePaths": SOURCES,
        "SourceHashes": hashes,
        "ExpectedCounts": counts,
        "PracticeGroups": tables["PracticeGroup"],
        "PracticeChapters": tables["PracticeChapter"],
        "PracticeActivities": tables["PracticeActivity"],
        "TeachingActivities": tables["TeachingActivity"],
        "TeachingRobots": tables["TeachingRobot"],
        "Stages": stages,
        "StageLevelControls": controls,
        "Robots": robots,
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
    tables, hashes = load_sources(args.source.resolve())
    catalog = build_catalog(tables, hashes)
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
