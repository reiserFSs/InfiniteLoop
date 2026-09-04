"""Derive version event catalogs and live schedule tables from authoritative tables and notices."""
from __future__ import annotations

import csv
import datetime as dt
import html
import json
import re
import subprocess
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

CATALOG_HEADER = ("Id", "TimeId", "SkipId", "Name", "ShowBeginTime", "Source")
SCHEDULE_HEADER = ("Id", "StartTime", "EndTime", "Source")
_NOTICE_SUFFIX = re.compile(r"\b(?:event now live|update note|update notice|now available|new mode)\b", re.I)
_NON_ALNUM = re.compile(r"[^a-z0-9]+")
_SHOW_BEGIN = re.compile(r"(\d{4})-(\d{2})-(\d{2})")
_EN_LIVE_VERSION = re.compile(r"^.*\[(\d+)\.(\d+)\.(\d+)\]\s+EN\s+LIVE\s*$")
_REGION_LIVE_VERSION = re.compile(r"^.*\[(\d+)\.(\d+)\.(\d+)\]\s+(EN|CN)\s+LIVE\s*$")
_TRANSFINITE_TABLE = "en/bytes/share/fuben/transfinite/TransfiniteActivity.json"
_DRAW_CAN_LIVER_TABLE = "en/bytes/share/draw/DrawCanLiverActivity.json"
_SPECIAL_ACTIVITY_TABLE = "client/activitybrief/SpecialActivity.json"
_FUBEN_CLIENT_CONFIG_TABLE = "client/fuben/FubenClientConfig.json"
_FUBEN_ACTIVITY_TIME_TIPS_TABLE = "client/fuben/FubenActivityTimeTips.json"



def normalized_name(value: str) -> str:
    value = _NOTICE_SUFFIX.sub("", value).strip()
    return _NON_ALNUM.sub("", value.lower())


def _rows(source: Path, relative: str, region: str = "en") -> list[dict[str, Any]]:
    value = json.loads((source / region / "bytes" / relative).read_text())
    if not isinstance(value, list):
        raise ValueError(f"{region}/bytes/{relative}: expected a JSON array")
    return value


def _int(value: Any) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) and value > 0 else None

def _show_begin_timestamp(value: Any) -> int | None:
    if not isinstance(value, str):
        return None
    match = _SHOW_BEGIN.search(value)
    if match is None:
        return None
    return int(dt.datetime(*map(int, match.groups()), tzinfo=dt.timezone.utc).timestamp())


def _add(records: list[tuple[int, int | None, int | None, str, int | None, str]], seen: set[tuple[int, int | None, int | None, str]], identity: Any, time_id: Any, skip_id: Any, name: Any, source: str, show_begin: Any = None) -> None:
    event_id = _int(identity)
    if event_id is None or not isinstance(name, str) or not name.strip():
        return
    record = (event_id, _int(time_id), _int(skip_id), name.strip(), _show_begin_timestamp(show_begin), source)
    key = record[:4]
    if key not in seen:
        seen.add(key)
        records.append(record)


def build_catalog(source: Path) -> list[tuple[int, int | None, int | None, str, int | None, str]]:
    """Return catalog rows. All identities come directly from versioned client tables."""
    records: list[tuple[int, int | None, int | None, str, int | None, str]] = []
    seen: set[tuple[int, int | None, int | None, str]] = set()
    for row in _rows(source, "client/activity/Activity.json"):
        params = row.get("Params")
        skip_id = params[0] if isinstance(params, list) and params else None
        _add(records, seen, row.get("Id"), row.get("TimeId"), skip_id, row.get("Name"), "client/activity/Activity", row.get("ShowBeginTime"))
    for row in _rows(source, "share/fuben/FubenActivity.json"):
        _add(records, seen, row.get("Id"), row.get("TimeId"), row.get("SkipId"), row.get("Name"), "share/fuben/FubenActivity")
    for row in _rows(source, "client/activitybrief/SpecialActivity.json"):
        _add(records, seen, row.get("Id"), row.get("TimeId"), row.get("SkipId"), row.get("Name") or row.get("ActivityType"), "client/activitybrief/SpecialActivity")
    for row in _rows(source, "client/activitybrief/ActivityBrief.json"):
        _add(records, seen, row.get("Id"), row.get("TimeId"), row.get("SkipId"), row.get("Name") or "ActivityBrief", "client/activitybrief/ActivityBrief")
    for row in _rows(source, "client/activitybrief/ActivityBriefGroup.json"):
        _add(records, seen, row.get("Id"), row.get("TimeId"), row.get("SkipId"), row.get("Name"), "client/activitybrief/ActivityBriefGroup")
    for row in _rows(source, "share/newactivitycalendar/NewActivityCalendarActivity.json"):
        _add(records, seen, row.get("ActivityId"), row.get("MainTimeId"), row.get("SkipId"), row.get("Name"), "share/newactivitycalendar/NewActivityCalendarActivity")
    chapters = {row.get("ChapterId"): row for row in _rows(source, "share/fuben/mainline2/MainLine2Chapter.json")}
    for row in _rows(source, "share/fuben/mainline2/MainLine2Main.json"):
        for chapter_id in row.get("ChapterIds", []):
            chapter = chapters.get(chapter_id, {})
            _add(records, seen, chapter_id, chapter.get("ActivityTimeId"), None, row.get("Name"), "share/fuben/mainline2/MainLine2Main+Chapter")
    for row in _rows(source, "share/fuben/transfinite/TransfiniteActivity.json"):
        _add(records, seen, row.get("Id"), row.get("TimeId"), row.get("SkipId"), "Transfinite", "share/fuben/transfinite/TransfiniteActivity")
    return records


def write_catalog(path: Path, rows: Iterable[tuple[int, int | None, int | None, str, int | None, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        writer.writerow(CATALOG_HEADER)
        writer.writerows((event_id, time_id or "", skip_id or "", name, show_begin or "", source) for event_id, time_id, skip_id, name, show_begin, source in rows)


def read_catalog(path: Path) -> list[dict[str, str]]:
    with path.open(newline="") as stream:
        reader = csv.DictReader(stream, delimiter="\t")
        if tuple(reader.fieldnames or ()) != CATALOG_HEADER:
            raise ValueError(f"{path}: invalid EventCatalog header")
        return list(reader)


def _notice_windows(notices: Iterable[dict[str, Any]]) -> dict[str, tuple[int, int, str]]:
    windows: dict[str, tuple[int, int, str]] = {}
    for notice in notices:
        title = notice.get("Title")
        begin, end = notice.get("BeginTime"), notice.get("EndTime")
        if not isinstance(title, str) or not isinstance(begin, int) or not isinstance(end, int) or begin >= end:
            continue
        name = normalized_name(title)
        if not name:
            continue
        previous = windows.get(name)
        if previous is None or begin > previous[0]:
            windows[name] = (begin, end, f"GameNotice:{notice.get('Id', title)}")
    return windows


def _catalog_show_begin_windows(catalog: Iterable[dict[str, str]], notices: Iterable[dict[str, Any]]) -> dict[str, tuple[int, int, str]]:
    update_ends = [notice.get("EndTime") for notice in notices if isinstance(notice.get("Title"), str) and "update note" in notice["Title"].lower() and isinstance(notice.get("EndTime"), int)]
    if not update_ends:
        return {}
    patch_end = max(update_ends)
    return {
        normalized_name(row["Name"]): (int(row["ShowBeginTime"]), patch_end, "EventCatalog.ShowBeginTime+GameNotice:update-note")
        for row in catalog
        if row.get("ShowBeginTime", "").isdigit() and int(row["ShowBeginTime"]) < patch_end
    }


def _update_note_event_windows(
    catalog: Iterable[dict[str, str]],
    notices: Iterable[dict[str, Any]],
    notice_root: Path,
) -> dict[str, tuple[int, int, str]]:
    """Extract explicitly dated event periods from the official update note."""
    output: dict[str, tuple[int, int, str]] = {}
    period = re.compile(
        r"Event Period:\s*([A-Z][a-z]+ \d{1,2}, \d{4}, \d{2}:\d{2})\s*-\s*"
        r"([A-Z][a-z]+ \d{1,2}, \d{4}, \d{2}:\d{2})\s*\(UTC\)",
        re.I,
    )
    names = {
        row["Name"]
        for row in catalog
        if isinstance(row.get("Name"), str) and len(row["Name"].strip()) >= 4
    }
    for notice in notices:
        title = notice.get("Title")
        if not isinstance(title, str) or "update note" not in title.lower():
            continue
        urls = [
            entry.get("Url")
            for entry in notice.get("Content", [])
            if isinstance(entry, dict) and isinstance(entry.get("Url"), str)
        ]
        if isinstance(notice.get("HtmlUrl"), str):
            urls.append(notice["HtmlUrl"])
        for html_url in urls:
            html_path = notice_root / html_url
            if not html_path.is_file():
                continue
            text = html.unescape(re.sub(r"<[^>]+>", " ", html_path.read_text(errors="replace")))
            text = " ".join(text.split())
            folded = text.casefold()
            for name in names:
                index = folded.find(name.casefold())
                if index < 0:
                    continue
                match = period.search(text, index, index + 1000)
                if match is None:
                    continue
                try:
                    start = int(dt.datetime.strptime(match.group(1), "%B %d, %Y, %H:%M").replace(tzinfo=dt.timezone.utc).timestamp())
                    end = int(dt.datetime.strptime(match.group(2), "%B %d, %Y, %H:%M").replace(tzinfo=dt.timezone.utc).timestamp())
                except ValueError:
                    continue
                if start < end:
                    output[normalized_name(name)] = (start, end, f"GameNotice:update-note-html:{html_path.name}")
    return output


def _catalog_notice_name_windows(catalog: Iterable[dict[str, str]], notices: Iterable[dict[str, Any]]) -> dict[str, tuple[int, int, str]]:
    """Map a client activity name to a public notice title that names it as a suffix."""
    catalog_names = sorted({normalized_name(row["Name"]) for row in catalog if row.get("Name")})
    output: dict[str, tuple[int, int, str]] = {}
    for notice_name, window in _notice_windows(notices).items():
        matches = [name for name in catalog_names if name and notice_name.endswith(name)]
        for name in matches:
            output[name] = window
    return output


def _theatre6_windows(source: Path) -> dict[int, tuple[int, int, str]]:
    pvp_activities = _rows(source, "share/theatre6pvp/Theatre6PvpActivity.json")
    client_config = _rows(source, "client/theatre6/Theatre6ClientConfig.json")
    consume_ids = next((row.get("Values", []) for row in client_config if row.get("Id") == "ConsumeId"), [])
    if not isinstance(consume_ids, list):
        return {}
    items = {row.get("Id"): row for row in _rows(source, "share/item/Item.json")}
    output: dict[int, tuple[int, int, str]] = {}
    for activity in pvp_activities:
        time_id = _int(activity.get("TimeId"))
        if time_id is None:
            continue
        for item_id in consume_ids:
            item = items.get(item_id)
            if not isinstance(item, dict) or "shrouded requiem" not in str(item.get("Description", "")).lower():
                continue
            start_text, duration = item.get("StartTime"), item.get("Duration")
            if not isinstance(start_text, str) or not isinstance(duration, int):
                continue
            try:
                start = int(dt.datetime.strptime(start_text, "%Y/%m/%d %H:%M").replace(tzinfo=dt.timezone.utc).timestamp())
            except ValueError:
                continue
            output[time_id] = (start, start + duration, f"feature-window:Theatre6PvpActivity+Theatre6ClientConfig+Item:{item_id}")
    if len(output) == 1:
        rank_time_ids = [
            time_id
            for row in sorted(
                _rows(source, "share/theatre6pvp/Theatre6PvpRank.json"),
                key=lambda row: _int(row.get("Id")) or 2**63 - 1,
            )
            if (time_id := _int(row.get("TimeId"))) is not None
        ]
        if rank_time_ids:
            start, end, provenance = next(iter(output.values()))
            output[rank_time_ids[0]] = (
                start,
                end,
                provenance + "+Theatre6PvpRank:first-positive-TimeId",
            )
    return output


def _git(source: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(source), *args],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise ValueError(f"unable to read EN release history: {result.stderr.strip()}")
    return result.stdout


def _release_commit(source: Path, relative: str, major: int, minor: int) -> tuple[str, str]:
    releases: list[tuple[int, str, str]] = []
    for line in _git(source, "log", "HEAD", "--format=%H%x09%s", "--", relative).splitlines():
        commit, separator, subject = line.partition("\t")
        match = _EN_LIVE_VERSION.match(subject)
        if not separator or match is None:
            continue
        release_major, release_minor, patch = map(int, match.groups())
        if (release_major, release_minor) == (major, minor):
            releases.append((patch, commit, f"{release_major}.{release_minor}.{patch}"))
    if not releases:
        raise ValueError(f"EN LIVE history for {relative} has no {major}.{minor} release")
    highest_patch = max(release[0] for release in releases)
    candidates = [release for release in releases if release[0] == highest_patch]
    # git-log order is the authoritative history order when a release was republished.
    return candidates[0][1], candidates[0][2]

def _release_table(source: Path, commit: str, relative: str) -> list[dict[str, Any]]:
    raw = _git(source, "show", f"{commit}:{relative}")
    try:
        rows = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError(f"{commit}:{relative}: invalid JSON") from exc
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows):
        raise ValueError(f"{commit}:{relative}: expected a JSON array")
    return rows


def _current_en_minor(source: Path) -> tuple[int, int]:
    subject = _git(source, "show", "-s", "--format=%s", "HEAD").strip()
    match = _EN_LIVE_VERSION.match(subject)
    if match is None:
        raise ValueError(f"source HEAD is not an EN LIVE semantic-version release: {subject!r}")
    major, minor, _ = map(int, match.groups())
    if minor < 2:
        raise ValueError(f"EN LIVE release {major}.{minor} has no two prior minor releases")
    return major, minor


def _most_recent_release_pair(source: Path, relative: str) -> tuple[str, str, str, str]:
    """Return the two most recent EN LIVE releases of a table (current, then base).

    Falls back to the latest release at-or-before the current EN minor, so tables
    that were not updated in the current minor still produce a determinable pair.
    """
    major, current_minor = _current_en_minor(source)
    releases: list[tuple[int, int, str, str]] = []
    for line in _git(source, "log", "HEAD", "--format=%H%x09%s", "--", relative).splitlines():
        commit, separator, subject = line.partition("\t")
        match = _EN_LIVE_VERSION.match(subject)
        if not separator or match is None:
            continue
        release_major, release_minor, patch = map(int, match.groups())
        if release_major != major or release_minor > current_minor:
            continue
        version = f"{release_major}.{release_minor}.{patch}"
        if not releases or releases[-1][2] != commit:
            releases.append((release_minor, patch, commit, version))
    if not releases:
        raise ValueError(f"EN LIVE history for {relative} has no release at-or-before {major}.{current_minor}")
    current_commit, current_version = releases[0][2], releases[0][3]
    if len(releases) == 1:
        base_commit, base_version = current_commit, current_version
    else:
        base_commit, base_version = releases[1][2], releases[1][3]
    return current_commit, current_version, base_commit, base_version


def _current_minor_regional_rows(
    source: Path,
    relative: str,
    regions: tuple[str, ...] = ("en", "cn"),
) -> tuple[list[dict[str, Any]], str]:
    """Read a current-minor table, preferring the requested region order."""
    major, minor = _current_en_minor(source)
    observed: list[str] = []
    for region in regions:
        repository_path = f"{region}/bytes/{relative}"
        best = None
        for line in _git(source, "log", "HEAD", "--format=%H%x09%s", "--", repository_path).splitlines():
            commit, separator, subject = line.partition("\t")
            match = _REGION_LIVE_VERSION.match(subject)
            if not separator or match is None or match.group(4).lower() != region:
                continue
            release_major, release_minor, patch = map(int, match.groups()[:3])
            version = f"{release_major}.{release_minor}.{patch}"
            if (release_major, release_minor) == (major, minor):
                # git-log order is the authoritative history order when republished.
                best = (commit, version)
                break
            observed.append(f"{repository_path}={version}")
        if best is not None:
            return _rows(source, relative, region), f"{region.upper()}-LIVE:{best[1]}@{best[0]}"
        observed.append(f"{repository_path}=no-{major}.{minor}-release")
    raise ValueError(
        f"no current {major}.{minor} regional source for {relative}: {', '.join(observed)}"
    )


def _maintenance_bounds(notices: Iterable[dict[str, Any]], login_notice_path: Path) -> tuple[int, int, int]:
    try:
        login_notice = json.loads(login_notice_path.read_text())
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"{login_notice_path}: unable to load official maintenance notice") from exc
    if not isinstance(login_notice, dict):
        raise ValueError(f"{login_notice_path}: expected a maintenance notice object")
    begin, end = login_notice.get("BeginTime"), login_notice.get("EndTime")
    if not isinstance(begin, int) or not isinstance(end, int) or begin >= end:
        raise ValueError(f"{login_notice_path}: invalid official maintenance bounds")
    update_ends = {
        notice["EndTime"]
        for notice in notices
        if isinstance(notice.get("Title"), str)
        and "update note" in notice["Title"].lower()
        and isinstance(notice.get("EndTime"), int)
        and notice["EndTime"] > end
    }
    if len(update_ends) != 1:
        raise ValueError("official update-note timing is unavailable or ambiguous for the current EN maintenance")
    return end, next(iter(update_ends)) + (end - begin), end - begin


def release_added_rows(
    source: Path,
    relative: str,
    identity: str = "Id",
    minor_offset: int = 1,
) -> tuple[str, str, str, str, list[dict[str, Any]]]:
    """Return rows added in a target EN minor relative to the preceding minor."""
    major, current_minor = _current_en_minor(source)
    if minor_offset < 0 or current_minor - minor_offset < 1:
        raise ValueError(f"invalid EN release-history minor offset: {minor_offset}")
    target_minor = current_minor - minor_offset
    target_commit, target_version = _release_commit(source, relative, major, target_minor)
    base_commit, base_version = _release_commit(source, relative, major, target_minor - 1)
    target_rows = _release_table(source, target_commit, relative)
    base_ids = {_int(row.get(identity)) for row in _release_table(source, base_commit, relative)}
    return (
        target_commit,
        target_version,
        base_commit,
        base_version,
        [row for row in target_rows if _int(row.get(identity)) not in base_ids],
    )


def _transfinite_windows(source: Path, notices: Iterable[dict[str, Any]], login_notice_path: Path) -> dict[int, tuple[int, int, str]]:
    prior_commit, prior_version, base_commit, base_version, added_rows = release_added_rows(
        source,
        _TRANSFINITE_TABLE,
    )
    current_rows = {_int(row.get("Id")): row for row in _rows(source, "share/fuben/transfinite/TransfiniteActivity.json")}
    additions = sorted(
        (row for row in added_rows if _int(row.get("TimeId")) is not None),
        key=lambda row: _int(row.get("Id")) or 0,
    )
    if not additions:
        raise ValueError(f"{prior_version} TransfiniteActivity has no positive-TimeId rows newly added since {base_version}")
    patch_start, patch_boundary, _ = _maintenance_bounds(notices, login_notice_path)
    output: dict[int, tuple[int, int, str]] = {}
    start = patch_start
    for row in additions:
        activity_id = _int(row.get("Id"))
        time_id = _int(row.get("TimeId"))
        cycle_seconds = _int(row.get("CycleSeconds"))
        current = current_rows.get(activity_id)
        if activity_id is None or time_id is None or cycle_seconds is None or current != row:
            raise ValueError(f"current TransfiniteActivity does not exactly retain {prior_version} rotation {activity_id}")
        next_start = start + cycle_seconds
        if next_start > patch_boundary:
            break
        output[time_id] = (
            start,
            next_start - 60,
            f"version-history:EN-LIVE:{prior_version}@{prior_commit}/TransfiniteActivity"
            f"+EN-LIVE:{base_version}@{base_commit}/TransfiniteActivity"
            "+LoginNotice:EndTime+GameNotice:update-note-EndTime+maintenance-duration+CycleSeconds",
        )
        start = next_start
    if not output:
        raise ValueError("no prior-release Transfinite rotations fit within the official patch boundary")
    return output


def _draw_can_liver_windows(
    source: Path,
    notices: Iterable[dict[str, Any]],
    login_notice_path: Path,
) -> dict[int, tuple[int, int, str]]:
    current_commit, current_version, base_commit, base_version = _most_recent_release_pair(
        source, _DRAW_CAN_LIVER_TABLE
    )
    current_rows = {_int(row.get("Id")): row for row in _rows(source, "share/draw/DrawCanLiverActivity.json")}
    base_ids = {
        _int(row.get("Id"))
        for row in _release_table(source, base_commit, _DRAW_CAN_LIVER_TABLE)
    }
    additions = [
        row for row in current_rows.values()
        if _int(row.get("Id")) not in base_ids and _int(row.get("TimeId")) is not None
    ]
    patch_start, patch_boundary, _ = _maintenance_bounds(notices, login_notice_path)
    output: dict[int, tuple[int, int, str]] = {}
    for row in additions:
        activity_id = _int(row.get("Id"))
        time_id = _int(row.get("TimeId"))
        if activity_id is None or time_id is None:
            raise ValueError(f"current DrawCanLiverActivity has an invalid row {activity_id}")
        output[time_id] = (
            patch_start,
            patch_boundary,
            f"version-history:EN-LIVE:{current_version}@{current_commit}/DrawCanLiverActivity"
            f"+EN-LIVE:{base_version}@{base_commit}/DrawCanLiverActivity"
            "+LoginNotice:EndTime+GameNotice:update-note-EndTime+maintenance-duration",
        )
    return output


def _activity_brief_windows(
    source: Path,
    notices: Iterable[dict[str, Any]],
    login_notice_path: Path,
) -> dict[int, tuple[int, int, str]]:
    """Use the current patch window for the top-level Events page."""
    patch_start, patch_boundary, _ = _maintenance_bounds(notices, login_notice_path)
    brief_rows = _rows(source, "client/activitybrief/ActivityBrief.json")
    briefs = [
        row
        for row in brief_rows
        if _int(row.get("TimeId")) is not None
    ]
    if len(briefs) != 1:
        raise ValueError(f"current ActivityBrief must expose exactly one positive TimeId, got {len(briefs)}")
    brief = briefs[0]
    parent_time_id = _int(brief.get("TimeId"))
    assert parent_time_id is not None
    provenance = (
        "client/activitybrief/ActivityBrief"
        "+LoginNotice:EndTime+GameNotice:update-note-EndTime+maintenance-duration"
    )
    output = {parent_time_id: (patch_start, patch_boundary, provenance)}
    current_group_ids = {
        group_id
        for group_id in brief.get("GroupIdList", [])
        if _int(group_id) is not None
    }
    mainline_time_ids = {
        time_id
        for row in _rows(source, "client/activitybrief/ActivityBriefGroup.json")
        if _int(row.get("Id")) in current_group_ids
        and row.get("BtnInitMethodName") == "RefreshActivityMainLine2"
        and (time_id := _int(row.get("TimeId"))) is not None
    }
    if len(mainline_time_ids) != 1:
        raise ValueError(
            f"current ActivityBrief must expose exactly one MainLine2 TimeId, got {sorted(mainline_time_ids)}"
        )
    output[next(iter(mainline_time_ids))] = (
        patch_start,
        patch_boundary,
        provenance + "+ActivityBriefGroup:RefreshActivityMainLine2",
    )
    return output


def _special_activity_windows(
    source: Path,
    catalog: Iterable[dict[str, str]],
    schedules: dict[int, tuple[int, int, str]],
    notices: Iterable[dict[str, Any]],
    login_notice_path: Path,
) -> dict[int, tuple[int, int, str]]:
    """Make client promo entries follow the scheduled feature reached by their SkipId."""
    patch_start, patch_end, _ = _maintenance_bounds(notices, login_notice_path)
    feature_time_ids_by_skip: dict[int, set[int]] = defaultdict(set)
    for row in catalog:
        try:
            time_id, skip_id = int(row["TimeId"]), int(row["SkipId"])
        except (KeyError, TypeError, ValueError):
            continue
        if time_id > 0 and skip_id > 0:
            feature_time_ids_by_skip[skip_id].add(time_id)

    output: dict[int, tuple[int, int, str]] = {}
    seen_presentations: set[str] = set()
    special_rows = sorted(
        _rows(source, _SPECIAL_ACTIVITY_TABLE),
        key=lambda row: _int(row.get("Id")) or 2**63 - 1,
    )
    for row in special_rows:
        time_id, skip_id = _int(row.get("TimeId")), _int(row.get("SkipId"))
        if time_id is None or skip_id is None or _int(row.get("OnlyRedPoint")) is not None or time_id in schedules:
            continue
        presentation = json.dumps(
            {key: value for key, value in row.items() if key not in {"Id", "TimeId"}},
            sort_keys=True,
            separators=(",", ":"),
        )
        if presentation in seen_presentations:
            continue
        seen_presentations.add(presentation)
        candidates = [
            (candidate_id, schedules[candidate_id])
            for candidate_id in feature_time_ids_by_skip.get(skip_id, ())
            if candidate_id in schedules
            and (schedules[candidate_id][1] == 0 or schedules[candidate_id][1] > patch_start)
            and (schedules[candidate_id][0] == 0 or schedules[candidate_id][0] < patch_end)
            and not schedules[candidate_id][2].startswith("GameNotice:update-note-html:")
        ]
        if not candidates:
            continue
        _, (start, end, provenance) = max(
            candidates,
            key=lambda candidate: (
                candidate[1][1] if candidate[1][1] != 0 else 2**63 - 1,
                candidate[1][0],
                candidate[0],
            ),
        )
        output[time_id] = (
            start,
            end,
            f"feature-window:SpecialActivity:SkipId={skip_id}+{provenance}",
        )
    return output

def _fuben_activity_time_tip_windows(
    source: Path,
    schedules: dict[int, tuple[int, int, str]],
    main_panel: dict[int, tuple[int, int, str]],
) -> dict[int, tuple[int, int, str]]:
    """Select the current complete tip combination in each component-schedule segment."""
    if len(main_panel) != 1:
        return {}
    panel_start, panel_end, _ = next(iter(main_panel.values()))
    tips, tips_source = _current_minor_regional_rows(source, _FUBEN_ACTIVITY_TIME_TIPS_TABLE)
    activities, activities_source = _current_minor_regional_rows(
        source, "share/fuben/FubenActivity.json"
    )
    activity_time_ids = {
        row["Name"]: time_id
        for row in activities
        if isinstance(row.get("Name"), str) and (time_id := _int(row.get("TimeId"))) is not None
    }
    components: list[tuple[int, int, set[int]]] = []
    for row in tips:
        tip_id, time_id, desc = _int(row.get("Id")), _int(row.get("TimeId")), row.get("Desc")
        if tip_id is None or time_id is None or not isinstance(desc, str):
            continue
        time_ids = {candidate for name, candidate in activity_time_ids.items() if name in desc}
        if not time_ids:
            raise ValueError(f"FubenActivityTimeTips {tip_id} names no FubenActivity")
        missing = sorted(time_id for time_id in time_ids if time_id not in schedules)
        if missing:
            raise ValueError(f"FubenActivityTimeTips {tip_id} has unscheduled components {missing}")
        components.append((tip_id, time_id, time_ids))
    boundaries = {panel_start, panel_end}
    for _, _, time_ids in components:
        for time_id in time_ids:
            start, end, _ = schedules[time_id]
            if start >= end:
                raise ValueError(f"FubenActivityTimeTips component {time_id} has invalid schedule")
            if panel_start < start < panel_end:
                boundaries.add(start)
            if panel_start < end < panel_end:
                boundaries.add(end)
    segment_boundaries = sorted(boundaries)
    selected: list[tuple[int, int, int]] = []
    for start, end in zip(segment_boundaries, segment_boundaries[1:]):
        eligible = [
            component
            for component in components
            if all(schedules[time_id][0] <= start and end <= schedules[time_id][1] for time_id in component[2])
        ]
        if eligible:
            tip_id, time_id, time_ids = max(
                eligible,
                key=lambda component: (
                    max(schedules[candidate][0] for candidate in component[2]),
                    len(component[2]),
                    -component[0],
                ),
            )
            if selected and selected[-1][0] == time_id:
                selected[-1] = (time_id, selected[-1][1], end)
            else:
                selected.append((time_id, start, end))
    provenance = (
        "feature-window:FubenActivityTimeTips:"
        f"{tips_source}+FubenActivity:{activities_source}+ActivitySchedule:complete-component-segment"
    )
    if len({time_id for time_id, _, _ in selected}) != len(selected):
        # EN tip components are not guaranteed to cover the whole panel window
        # contiguously; a non-contiguous selection is a data reality, not a defect.
        return {}
    return {time_id: (start, end, provenance) for time_id, start, end in selected}


def _named_event_period(
    notice_root: Path,
    notices: Iterable[dict[str, Any]],
    event_name: str,
) -> tuple[int, int, str] | None:
    """Return the official Event Period of a named update-note event (article HTML)."""
    period = re.compile(
        r"Event Period:\s*([A-Z][a-z]+ \d{1,2}, \d{4}, \d{2}:\d{2})\s*-\s*"
        r"([A-Z][a-z]+ \d{1,2}, \d{4}, \d{2}:\d{2})\s*\(UTC\)",
        re.I,
    )
    for notice in notices:
        if not isinstance(notice.get("Title"), str) or "update note" not in notice["Title"].lower():
            continue
        urls = [
            entry.get("Url")
            for entry in notice.get("Content", [])
            if isinstance(entry, dict) and isinstance(entry.get("Url"), str)
        ]
        if isinstance(notice.get("HtmlUrl"), str):
            urls.append(notice["HtmlUrl"])
        for html_url in urls:
            html_path = notice_root / html_url
            if not html_path.is_file():
                continue
            text = html.unescape(re.sub(r"<[^>]+>", " ", html_path.read_text(errors="replace")))
            text = " ".join(text.split())
            index = text.casefold().find(event_name.casefold())
            if index < 0:
                continue
            match = period.search(text, index, index + 1000)
            if match is None:
                continue
            try:
                start = int(dt.datetime.strptime(match.group(1), "%B %d, %Y, %H:%M").replace(tzinfo=dt.timezone.utc).timestamp())
                end = int(dt.datetime.strptime(match.group(2), "%B %d, %Y, %H:%M").replace(tzinfo=dt.timezone.utc).timestamp())
            except ValueError:
                continue
            if start < end:
                return start, end, f"GameNotice:update-note-html:{html_path.name}"
    return None


def _concert_preheating_windows(
    source: Path,
    notices: Iterable[dict[str, Any]],
    notice_root: Path,
) -> dict[int, tuple[int, int, str]]:
    """Schedule the current ConcertPreHeating activity from the article's Strings Workshop period."""
    concert = _rows(source, "share/miniactivity/musicgame/concertpreheating/ConcertPreHeatingActivity.json")
    time_ids = sorted({_int(row.get("TimeId")) for row in concert if _int(row.get("TimeId")) is not None})
    if len(time_ids) != 1:
        raise ValueError(f"current ConcertPreHeating must expose exactly one positive TimeId, got {time_ids}")
    period = _named_event_period(notice_root, notices, "Strings Workshop")
    if period is None:
        raise ValueError("Strings Workshop Event Period is absent from the official update note")
    start, end, provenance = period
    return {time_ids[0]: (start, end, provenance + "+ConcertPreHeatingActivity")}


def _signin_windows(
    source: Path,
    notices: Iterable[dict[str, Any]],
    notice_root: Path,
) -> dict[int, tuple[int, int, str]]:
    """Schedule the current 4.7 event sign-in (Id 115) from the article's 7-Day Sign-in period."""
    signins = _rows(source, "share/signin/SignIn.json")
    current = [row for row in signins if row.get("Id") == 115 and _int(row.get("TimeId")) is not None]
    if len(current) != 1:
        raise ValueError("current SignIn 115 must expose exactly one positive TimeId")
    time_id = _int(current[0]["TimeId"])
    assert time_id is not None
    period = _named_event_period(notice_root, notices, "7-Day Sign-in")
    if period is None:
        raise ValueError("7-Day Sign-in Event Period is absent from the official update note")
    start, end, provenance = period
    return {time_id: (start, end, provenance + "+SignIn:Id=115")}


def _main_panel_window(
    source: Path,
    schedules: dict[int, tuple[int, int, str]],
    notices: Iterable[dict[str, Any]],
    login_notice_path: Path,
) -> dict[int, tuple[int, int, str]]:
    """Keep the client-configured Battle activity card open while a promo feature is scheduled."""
    config_rows, config_source = _current_minor_regional_rows(source, _FUBEN_CLIENT_CONFIG_TABLE)
    config = [row for row in config_rows if row.get("Key") == "MainPanelTimeId"]
    if len(config) != 1:
        raise ValueError("FubenClientConfig must define exactly one MainPanelTimeId")
    values = config[0].get("Values")
    alias_time_id = _int(values[0]) if isinstance(values, list) and len(values) == 1 else None
    if alias_time_id is None:
        raise ValueError("FubenClientConfig MainPanelTimeId must contain one positive integer")

    special_time_ids = {
        time_id
        for row in _rows(source, _SPECIAL_ACTIVITY_TABLE)
        if _int(row.get("OnlyRedPoint")) is None
        and (time_id := _int(row.get("TimeId"))) is not None
    }
    windows = [schedules[time_id] for time_id in special_time_ids if time_id in schedules]
    if not windows:
        return {}
    patch_start, patch_end, _ = _maintenance_bounds(notices, login_notice_path)
    first_start = 0 if any(window[0] == 0 for window in windows) else min(window[0] for window in windows)
    last_end = 0 if any(window[1] == 0 for window in windows) else max(window[1] for window in windows)
    start = max(patch_start, first_start)
    end = patch_end if last_end == 0 else min(patch_end, last_end)
    return {
        alias_time_id: (
            start,
            end,
            f"feature-window:FubenClientConfig.MainPanelTimeId:{config_source}+SpecialActivity",
        )
    }


def build_schedule(
    catalog: list[dict[str, str]],
    notices: Iterable[dict[str, Any]],
    source: Path | None = None,
    login_notice_path: Path | None = None,
    notice_root: Path | None = None,
) -> list[tuple[int, int, int, str]]:
    """Join client identities to authoritative feature windows; omit unresolved rows."""
    notice_list = list(notices)
    windows = _notice_windows(notice_list)
    for name, window in _catalog_show_begin_windows(catalog, notice_list).items():
        windows.setdefault(name, window)
    windows.update(_catalog_notice_name_windows(catalog, notice_list))
    if notice_root is not None:
        for name, window in _update_note_event_windows(catalog, notice_list, notice_root).items():
            windows.setdefault(name, window)
    by_name: dict[str, list[dict[str, str]]] = defaultdict(list)
    by_skip: dict[str, list[dict[str, str]]] = defaultdict(list)
    for row in catalog:
        if row.get("TimeId", "").isdigit() and int(row["TimeId"]) > 0:
            by_name[normalized_name(row["Name"])].append(row)
            if row.get("SkipId", "").isdigit() and int(row["SkipId"]) > 0:
                by_skip[row["SkipId"]].append(row)
    output: dict[int, tuple[int, int, str]] = {}
    for name, (start, end, provenance) in windows.items():
        matched = by_name.get(name, [])
        skips = {row["SkipId"] for row in matched if row.get("SkipId", "").isdigit() and int(row["SkipId"]) > 0}
        related = list(matched)
        if not provenance.startswith("GameNotice:update-note-html:"):
            for skip in skips:
                related.extend(by_skip[skip])
        for row in related:
            if normalized_name(row["Name"]) != name and normalized_name(row["Name"]) in windows:
                continue
            output[int(row["TimeId"])] = (start, end, provenance)
    if source is not None:
        output.update(_theatre6_windows(source))
        if login_notice_path is None:
            raise ValueError("official LoginNotice path is required for Transfinite schedule generation")
        output.update(_transfinite_windows(source, notice_list, login_notice_path))
        output.update(_draw_can_liver_windows(source, notice_list, login_notice_path))
        output.update(_activity_brief_windows(source, notice_list, login_notice_path))
        if notice_root is None:
            raise ValueError("official notice root is required for article-derived schedule generation")
        output.update(_concert_preheating_windows(source, notice_list, notice_root))
        output.update(_signin_windows(source, notice_list, notice_root))
        output.update(_special_activity_windows(source, catalog, output, notice_list, login_notice_path))
        main_panel = _main_panel_window(source, output, notice_list, login_notice_path)
        output.update(main_panel)
        output.update(_fuben_activity_time_tip_windows(source, output, main_panel))
    return [(time_id, *output[time_id]) for time_id in sorted(output)]


def write_schedule(path: Path, rows: Iterable[tuple[int, int, int, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        writer.writerow(SCHEDULE_HEADER)
        writer.writerows(rows)


def _read_schedule(path: Path) -> dict[int, tuple[int, int, str]]:
    if not path.is_file():
        return {}
    with path.open(newline="") as stream:
        reader = csv.DictReader(stream, delimiter="\t")
        if tuple(reader.fieldnames or ()) != SCHEDULE_HEADER:
            raise ValueError(f"{path}: invalid ActivitySchedule header")
        output: dict[int, tuple[int, int, str]] = {}
        for row in reader:
            try:
                time_id, start, end = int(row["Id"]), int(row["StartTime"]), int(row["EndTime"])
            except (KeyError, TypeError, ValueError) as exc:
                raise ValueError(f"{path}: invalid ActivitySchedule row") from exc
            source = row.get("Source")
            if time_id > 0 and isinstance(source, str):
                output[time_id] = (start, end, source)
        return output


def _preserve_refresh_derived_windows(
    refreshed: list[tuple[int, int, int, str]],
    existing: dict[int, tuple[int, int, str]],
) -> list[tuple[int, int, int, str]]:
    output = {time_id: (start, end, source) for time_id, start, end, source in refreshed}
    for time_id, (start, end, source) in existing.items():
        if source.startswith("feature-window:") or source.startswith("version-history:"):
            output[time_id] = (start, end, source)
    return [(time_id, *output[time_id]) for time_id in sorted(output)]


def regenerate_from_catalog(catalog_path: Path, notice_path: Path, schedule_path: Path) -> None:
    notices = json.loads(notice_path.read_text())
    if not isinstance(notices, list):
        raise ValueError(f"{notice_path}: expected a JSON array")
    refreshed = build_schedule(read_catalog(catalog_path), notices, notice_root=notice_path.parent)
    write_schedule(schedule_path, _preserve_refresh_derived_windows(refreshed, _read_schedule(schedule_path)))


def generate(
    source: Path,
    notice_path: Path,
    catalog_path: Path,
    schedule_path: Path,
    login_notice_path: Path,
) -> None:
    catalog_rows = build_catalog(source)
    write_catalog(catalog_path, catalog_rows)
    notices = json.loads(notice_path.read_text())
    if not isinstance(notices, list):
        raise ValueError(f"{notice_path}: expected a JSON array")
    write_schedule(
        schedule_path,
        build_schedule(read_catalog(catalog_path), notices, source, login_notice_path, notice_path.parent),
    )
