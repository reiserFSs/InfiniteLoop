import datetime as dt
import json
import tempfile
import unittest
from pathlib import Path

from Scripts import activity_schedule


class GodfallCalendarTests(unittest.TestCase):
    def test_permanent_windows_stay_inside_windows_localtime_range(self):
        windows = {row[0]: row[1:3] for row in activity_schedule.build_schedule([], [])}
        self.assertEqual(set(windows), {34, 35, 46401})
        now = dt.datetime(2026, 9, 11, tzinfo=dt.timezone.utc).timestamp()
        for start, end in windows.values():
            self.assertLessEqual(start, now)
            self.assertGreater(end, now)
            for offset_hours in (-12, 14):
                local = dt.datetime.fromtimestamp(end, dt.timezone(dt.timedelta(hours=offset_hours)))
                self.assertLessEqual(local.year, 3000)


class Theatre6MissionWindowTests(unittest.TestCase):
    def test_retired_token_does_not_displace_the_live_season_window(self):
        """A mission tab's token lifetime must not overwrite the PvP window of a newer token."""
        tables = {
            "share/theatre6pvp/Theatre6PvpActivity.json": [{"Id": 1, "TimeId": 48601}],
            "client/theatre6/Theatre6ClientConfig.json": [{"Id": "ConsumeId", "Values": [97090]}],
            "share/theatre6pvp/Theatre6PvpRank.json": [{"Id": 1, "TimeId": 48602}],
            "share/theatre6/Theatre6Reward.json": [{"Id": 4, "TaskTimeLimitId": 708}, {"Id": 5, "TaskTimeLimitId": 735}],
            "share/task/TaskTimeLimit.json": [
                {"Id": 708, "TimeId": 47121, "TaskId": [140118]},
                {"Id": 735, "TimeId": 48601, "TaskId": [140212]},
            ],
            "share/task/Task.json": [
                {"Id": 140118, "RewardId": 69117},
                {"Id": 140212, "RewardId": 69262},
            ],
            "share/reward/Reward.json": [
                {"Id": 69117, "SubIds": [691170]},
                {"Id": 69262, "SubIds": [692620]},
            ],
            "share/reward/RewardGoods.json": [
                {"Id": 691170, "TemplateId": 97100},
                {"Id": 692620, "TemplateId": 97101},
            ],
            "share/item/Item.json": [
                {"Id": 97090, "Description": "Live season [Shrouded Requiem] token", "StartTime": "2027/3/2 5:00", "Duration": 172800},
                {"Id": 97100, "StartTime": "2027/1/2 5:00", "Duration": 86400},
                {"Id": 97101, "StartTime": "2027/7/7 5:00", "Duration": 86400},
            ],
        }
        with tempfile.TemporaryDirectory() as root:
            source = Path(root)
            for relative, rows in tables.items():
                path = source / "en" / "bytes" / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(json.dumps(rows))
            windows = activity_schedule._theatre6_windows(source)

        def interval(start: str, duration: int) -> tuple[int, int]:
            epoch = int(dt.datetime.strptime(start, "%Y/%m/%d %H:%M").replace(tzinfo=dt.timezone.utc).timestamp())
            return epoch, epoch + duration

        self.assertEqual(set(windows), {47121, 48601, 48602})
        self.assertEqual(windows[47121][:2], interval("2027/1/2 5:00", 86400))
        self.assertEqual(windows[48601][:2], interval("2027/3/2 5:00", 172800))
        self.assertEqual(windows[48602][:2], interval("2027/3/2 5:00", 172800))


if __name__ == "__main__":
    unittest.main()
