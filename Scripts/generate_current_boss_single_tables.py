#!/usr/bin/env python3
"""Generate minimal Pain Cage TSV tables from the current EN client tables."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Iterable

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = REPO_ROOT.parent / "PGR_DATA" / "en" / "bytes" / "share"
DEFAULT_OUTPUT = REPO_ROOT / "Resources" / "table" / "share" / "fuben" / "bosssingle"
BOSS_FILES = (
    "BossSingleGrade", "BossSingleGroup", "BossSingleSection", "BossSingleStage",
    "BossSingleScoreRule", "BossSingleScoreReward", "BossSingleTrialGrade",
)


def load_rows(path: Path) -> list[dict[str, Any]]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"{path}: unable to load JSON: {exc}") from exc
    if not isinstance(value, list):
        raise ValueError(f"{path}: expected a JSON array")
    for index, row in enumerate(value):
        if not isinstance(row, dict):
            raise ValueError(f"{path}: row {index} is not an object")
    return value


def integer(row: dict[str, Any], field: str, source: Path, *, default: int | None = None) -> int:
    if field not in row:
        if default is not None:
            return default
        raise ValueError(f"{source}: row {row!r} is missing {field}")
    value = row[field]
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValueError(f"{source}: {field} must be an integer, got {value!r}")
    return value


def boolean01(row: dict[str, Any], field: str, source: Path, *, default: bool = False) -> int:
    value = row.get(field, default)
    if not isinstance(value, bool):
        raise ValueError(f"{source}: {field} must be a boolean, got {value!r}")
    return int(value)


def number_list(row: dict[str, Any], field: str, source: Path, *, floats: bool = False) -> list[str | int]:
    value = row.get(field)
    if not isinstance(value, list):
        raise ValueError(f"{source}: {field} must be an array")
    result: list[str | int] = []
    for item in value:
        if isinstance(item, bool) or not isinstance(item, (int, float) if floats else int):
            raise ValueError(f"{source}: {field} contains invalid value {item!r}")
        if floats:
            text = format(item, ".15g")
            result.append(text + ".0" if text.lstrip("-").isdigit() else text)
        else:
            result.append(item)
    return result


def unique_index(rows: list[dict[str, Any]], field: str, source: Path) -> dict[int, dict[str, Any]]:
    result: dict[int, dict[str, Any]] = {}
    for row in rows:
        key = integer(row, field, source)
        if key in result:
            raise ValueError(f"{source}: duplicate {field} {key}")
        result[key] = row
    return result


def scalar(value: Any) -> str:
    text = str(value)
    if "\t" in text or "\r" in text or "\n" in text:
        raise ValueError(f"TSV scalar contains a tab or newline: {text!r}")
    return text


def table(columns: list[str], rows: Iterable[list[Any]]) -> bytes:
    lines = ["\t".join(columns)]
    for row in rows:
        if len(row) != len(columns):
            raise ValueError(f"internal error: expected {len(columns)} columns, got {len(row)}")
        lines.append("\t".join(scalar(value) for value in row))
    return ("\n".join(lines) + "\n").encode("utf-8")


def repeated_columns(name: str, width: int) -> list[str]:
    return [f"{name}[{index}]" for index in range(1, width + 1)]


def padded(values: list[Any], width: int) -> list[Any]:
    return values + [""] * (width - len(values))


def generate(source: Path) -> dict[str, bytes]:
    boss_dir = source / "fuben" / "bosssingle"
    paths = {name: boss_dir / f"{name}.json" for name in BOSS_FILES}
    data = {name: load_rows(path) for name, path in paths.items()}
    stage_path = source / "fuben" / "Stage.json"
    reward_path = source / "reward" / "Reward.json"
    goods_path = source / "reward" / "RewardGoods.json"
    stages = unique_index(load_rows(stage_path), "StageId", stage_path)
    rewards = unique_index(load_rows(reward_path), "Id", reward_path)
    goods = unique_index(load_rows(goods_path), "Id", goods_path)
    config_path = source / "config" / "Config.json"
    config_rows = load_rows(config_path)
    config_values: dict[str, int] = {}
    wanted_config = {
        "BossSingleAutoFightNewCount": "AutoFightCount",
        "BossSingleAutoFightRebate": "AutoFightRebate",
    }
    for row in config_rows:
        key = row.get("Key")
        if key not in wanted_config:
            continue
        if key in config_values:
            raise ValueError(f"{config_path}: duplicate Key {key}")
        if row.get("Type") != "int":
            raise ValueError(f"{config_path}: {key} must have Type 'int', got {row.get('Type')!r}")
        config_values[key] = integer(row, "Value", config_path)
    missing_config = wanted_config.keys() - config_values.keys()
    if missing_config:
        raise ValueError(f"{config_path}: missing keys {', '.join(sorted(missing_config))}")

    output: dict[str, bytes] = {}
    output["BossSingleConfig.tsv"] = table(
        ["Id", "AutoFightCount", "AutoFightRebate"],
        [[1, config_values["BossSingleAutoFightNewCount"], config_values["BossSingleAutoFightRebate"]]],
    )
    grades = sorted(data["BossSingleGrade"], key=lambda r: (integer(r, "LevelType", paths["BossSingleGrade"]), integer(r, "GradeType", paths["BossSingleGrade"])))
    unique_index(grades, "LevelType", paths["BossSingleGrade"])
    width = max((len(number_list(r, "GroupId", paths["BossSingleGrade"])) for r in grades), default=0)
    grade_cols = ["LevelType", "GradeType", "MinPlayerLevel", "MaxPlayerLevel", "PreGradeType", "NeedScore", "ChallengeCount", "WeekChallengeCount", "StaminaCount", "RewardGroupId", "AfreshId", "InheritLevelType"] + repeated_columns("GroupId", width)
    output["BossSingleGrade.tsv"] = table(grade_cols, ([integer(r, f, paths["BossSingleGrade"], default=0) for f in grade_cols[:12]] + padded(number_list(r, "GroupId", paths["BossSingleGrade"]), width) for r in grades))

    def list_table(name: str, key: str, scalar_fields: list[str], list_field: str) -> bytes:
        source_path = paths[name]
        rows = sorted(data[name], key=lambda r: integer(r, key, source_path))
        unique_index(rows, key, source_path)
        list_width = max((len(number_list(r, list_field, source_path)) for r in rows), default=0)
        columns = scalar_fields + repeated_columns(list_field, list_width)
        return table(columns, ([integer(r, f, source_path, default=0) for f in scalar_fields] + padded(number_list(r, list_field, source_path), list_width) for r in rows))

    output["BossSingleGroup.tsv"] = list_table("BossSingleGroup", "Id", ["Id"], "SectionId")
    output["BossSingleSection.tsv"] = list_table("BossSingleSection", "Id", ["Id", "SectionId", "AfreshId"], "StageId")

    boss_stages = sorted(data["BossSingleStage"], key=lambda r: integer(r, "StageId", paths["BossSingleStage"]))
    unique_index(boss_stages, "StageId", paths["BossSingleStage"])
    stage_cols = ["StageId", "Score", "BossLoseHpScore", "LeftTimeScore", "LeftHpScore", "DifficultyType", "AutoFight", "RebootId", "PassTimeLimit"]
    stage_rows = []
    for row in boss_stages:
        stage_id = integer(row, "StageId", paths["BossSingleStage"])
        joined = stages.get(stage_id)
        if joined is None:
            raise ValueError(f"{stage_path}: missing StageId {stage_id} referenced by BossSingleStage")
        stage_rows.append([stage_id] + [integer(row, f, paths["BossSingleStage"]) for f in stage_cols[1:6]] + [boolean01(row, "AutoFight", paths["BossSingleStage"]), integer(joined, "RebootId", stage_path, default=0), integer(joined, "PassTimeLimit", stage_path, default=0)])
    output["BossSingleStage.tsv"] = table(stage_cols, stage_rows)

    rules = sorted(data["BossSingleScoreRule"], key=lambda r: integer(r, "Id", paths["BossSingleScoreRule"]))
    unique_index(rules, "Id", paths["BossSingleScoreRule"])
    specs = [("BossLoseHp", True), ("BossLoseHpScore", False), ("LeftTime", False), ("LeftTimeScore", True), ("CharLeftHp", True), ("CharLeftHpSocre", True)]
    widths = {field: max((len(number_list(r, field, paths["BossSingleScoreRule"], floats=is_float)) for r in rules), default=0) for field, is_float in specs}
    rule_cols = ["Id", "BaseScore"] + [column for field, _ in specs for column in repeated_columns(field, widths[field])]
    rule_rows = []
    for row in rules:
        values: list[Any] = [integer(row, "Id", paths["BossSingleScoreRule"]), integer(row, "BaseScore", paths["BossSingleScoreRule"], default=0)]
        for field, is_float in specs:
            values.extend(padded(number_list(row, field, paths["BossSingleScoreRule"], floats=is_float), widths[field]))
        rule_rows.append(values)
    output["BossSingleScoreRule.tsv"] = table(rule_cols, rule_rows)

    score_rewards = sorted(data["BossSingleScoreReward"], key=lambda r: integer(r, "Id", paths["BossSingleScoreReward"]))
    unique_index(score_rewards, "Id", paths["BossSingleScoreReward"])
    reward_cols = ["Id", "LevelType", "Score", "RewardId", "RewardGroupId"]
    output["BossSingleScoreReward.tsv"] = table(reward_cols, ([integer(r, f, paths["BossSingleScoreReward"], default=0) for f in reward_cols] for r in score_rewards))

    trials = sorted(data["BossSingleTrialGrade"], key=lambda r: integer(r, "LevelType", paths["BossSingleTrialGrade"]))
    unique_index(trials, "LevelType", paths["BossSingleTrialGrade"])
    trial_width = max((len(number_list(r, "SectionId", paths["BossSingleTrialGrade"])) for r in trials), default=0)
    trial_cols = ["LevelType", "IsBestiaryCfg"] + repeated_columns("SectionId", trial_width)
    output["BossSingleTrialGrade.tsv"] = table(trial_cols, ([integer(r, "LevelType", paths["BossSingleTrialGrade"]), boolean01(r, "IsBestiaryCfg", paths["BossSingleTrialGrade"])] + padded(number_list(r, "SectionId", paths["BossSingleTrialGrade"]), trial_width) for r in trials))

    relation_rows: list[list[int]] = []
    seen_relations: set[tuple[int, int]] = set()
    for row in score_rewards:
        score_reward_id = integer(row, "Id", paths["BossSingleScoreReward"])
        reward_id = integer(row, "RewardId", paths["BossSingleScoreReward"])
        reward = rewards.get(reward_id)
        if reward is None:
            raise ValueError(f"{reward_path}: missing Reward Id {reward_id} referenced by ScoreReward {score_reward_id}")
        sub_ids = number_list(reward, "SubIds", reward_path)
        for goods_id in sub_ids:
            assert isinstance(goods_id, int)
            relation = (score_reward_id, goods_id)
            if relation in seen_relations:
                raise ValueError(f"duplicate ScoreRewardId/GoodsId relation {relation}")
            seen_relations.add(relation)
            goods_row = goods.get(goods_id)
            if goods_row is None:
                raise ValueError(f"{goods_path}: missing RewardGoods Id {goods_id} referenced by Reward {reward_id}")
            relation_rows.append([score_reward_id, goods_id, integer(goods_row, "TemplateId", goods_path), integer(goods_row, "Count", goods_path)])
    relation_rows.sort(key=lambda r: (r[0], r[1]))
    output["BossSingleRewardGoods.tsv"] = table(["ScoreRewardId", "GoodsId", "TemplateId", "Count"], relation_rows)
    return output


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true", help="fail if generated files differ from disk")
    args = parser.parse_args()
    try:
        generated = generate(args.source.resolve())
        if args.check:
            mismatches = [name for name, content in generated.items() if not (args.output / name).is_file() or (args.output / name).read_bytes() != content]
            if mismatches:
                raise ValueError("generated output differs: " + ", ".join(mismatches))
            print(f"checked {len(generated)} byte-stable tables in {args.output}")
        else:
            args.output.mkdir(parents=True, exist_ok=True)
            for name, content in generated.items():
                (args.output / name).write_bytes(content)
            print("generated " + ", ".join(f"{name} ({content.count(bytes([10])) - 1} rows)" for name, content in generated.items()))
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
