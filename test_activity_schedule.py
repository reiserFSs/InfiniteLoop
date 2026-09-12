import datetime as dt
import unittest

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


if __name__ == "__main__":
    unittest.main()
