using AscNet.GameServer.Game;
using AscNet.Table.V2.share.passport;

namespace AscNet.Test;

internal static partial class Program
{
    // 4.9 adds retail rows whose TimeIds have no authored window yet (Docs/closed.md, "4.9 client tables without
    // server support"). Those rows stay closed; schedule-driven flows run on seasons with a bounded-or-policy window; permanent (0,0) entries are excluded.
    private static List<PassportActivityTable> ScheduledPassportSeasons() =>
        AscNet.Common.Util.TableReaderV2.Parse<PassportActivityTable>()
            .Where(row => row.TimeId is > 0 && ActivityScheduleService.TryGet(row.TimeId.Value, out ActivityScheduleEntry window)
                && window.EndTime > window.StartTime)
            .OrderBy(row => row.Id).ToList();

    // TEST-ONLY: 4.9 retail zeroed the TimeIds of ended seasons. The flows below were written against their last
    // authored windows, so they run with those TimeIds restored on the cached in-memory rows (authors no date; the
    // windows come from ActivitySchedule). The real 4.9 rows are asserted separately before the scope opens.
    private sealed class RetailTimeIdScope : IDisposable
    {
        private readonly List<(object Row, System.Reflection.PropertyInfo Property, object? Value)> saved = [];

        public RetailTimeIdScope(string propertyName, int timeId, IEnumerable<object> rows)
        {
            foreach (object row in rows)
            {
                System.Reflection.PropertyInfo property = row.GetType().GetProperty(propertyName)!;
                saved.Add((row, property, property.GetValue(row)));
                property.SetValue(row, Convert.ChangeType(timeId, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
            }
        }

        public void Dispose()
        {
            foreach ((object row, System.Reflection.PropertyInfo property, object? value) in saved)
                property.SetValue(row, value);
        }
    }
}
