using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.share.dormitory;
using AscNet.Table.V2.share.dormitory.furniture;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal partial class DormModule
{
    private static readonly Lazy<Dictionary<uint, FurnitureTable>> DormFurnitureById = new(() =>
        FurnitureData.Value.Furniture.ToDictionary(row => (uint)row.Id));
    private static readonly Lazy<ILookup<int, FurnitureLevelTable>> DormFurnitureLevels = new(() =>
        FurnitureData.Value.Levels.ToLookup(row => row.FurnitureType));
    private static readonly Lazy<Dictionary<int, FurnitureAdditionalAttrTable>> DormAdditionalAttrs = new(() =>
        FurnitureData.Value.AdditionalAttrs.ToDictionary(row => row.AttributeId));
    private static readonly Lazy<Dictionary<int, Dictionary<uint, int>>> DormTemplateRequirements = new(() =>
        TableReaderV2.Parse<DormTemplateTable>().ToDictionary(row => row.Id, row => row.FurnitureId
            .Where(id => id > 0).GroupBy(id => (uint)id).ToDictionary(group => group.Key, group => group.Count())));
    private static readonly Lazy<int> ResidentMoodTarget = new(() => TableReaderV2.Parse<ConditionTable>()
        .Where(row => row.Type == 29013).Select(row => row.Params[2]).Distinct().Single() * 100);

    private static bool TrackResidentMood(PlayerDormCharacter character, uint now)
    {
        uint previous = character.HighMoodSince;
        if (character.DormitoryId <= 0 || character.Mood < ResidentMoodTarget.Value)
            character.HighMoodSince = 0;
        else if (previous == 0 || previous > now)
            character.HighMoodSince = now;
        return previous != character.HighMoodSince;
    }

    private static List<int> RoomAttributes(IEnumerable<PlayerDormFurniture> furniture)
    {
        List<int> total = [0, 0, 0];
        foreach (PlayerDormFurniture piece in furniture)
        {
            var addition = DormAdditionalAttrs.Value.GetValueOrDefault(piece.Addition);
            for (int i = 0; i < total.Count; i++)
                total[i] += piece.AttrList.ElementAtOrDefault(i)
                    + (addition is { AddType: 2 } ? addition.AddValue.ElementAtOrDefault(i) : 0);
        }
        return total;
    }

    internal sealed class TaskCounts(PlayerDormState dorm)
    {
        private readonly Lazy<ILookup<int, PlayerDormFurniture>> placed = new(() =>
        {
            var rooms = dorm.Rooms.Select(room => (int)room.Id).ToHashSet();
            return dorm.Furniture.Where(piece => rooms.Contains(piece.DormitoryId)).ToLookup(piece => piece.DormitoryId);
        });
        private readonly Lazy<List<(int Quality, bool Placed)>> qualities = new(() =>
        {
            var rooms = dorm.Rooms.Select(room => (int)room.Id).ToHashSet();
            List<(int, bool)> result = [];
            foreach (PlayerDormFurniture piece in dorm.Furniture)
            {
                if (!DormFurnitureById.Value.TryGetValue(piece.ConfigId, out var config))
                    continue;
                int score = Score(piece);
                var level = DormFurnitureLevels.Value[config.TypeId].FirstOrDefault(
                    row => score >= (row.MinScore ?? 0) && score < row.MaxScore);
                if (level is not null)
                    result.Add((level.Quality, rooms.Contains(piece.DormitoryId)));
            }
            return result;
        });
        private readonly Lazy<ILookup<int, uint>> sets = new(() => dorm.FurnitureUnlocks.Concat(dorm.Furniture.Select(piece => piece.ConfigId)).Distinct()
            .Where(id => DormFurnitureById.Value.ContainsKey(id))
            .ToLookup(id => DormFurnitureById.Value[id].SuitId ?? 0));
        private readonly Lazy<List<int[]>> roomAttributes = new(() => dorm.Rooms.Select(room =>
        {
            List<int> attrs = RoomAttributes(dorm.Furniture.Where(piece => piece.DormitoryId == room.Id));
            return new[] { attrs[0], attrs[1], attrs[2], attrs.Sum() };
        }).ToList());
        private readonly Lazy<int> residents = new(() =>
        {
            var rooms = dorm.Rooms.Select(room => (int)room.Id).ToHashSet();
            return dorm.Characters.Count(character => rooms.Contains(character.DormitoryId));
        });

        public int Rooms => dorm.Rooms.Count;
        public int Residents => residents.Value;
        public int RoomsWithAttribute(int threshold, int category) =>
            category is >= 1 and <= 4 ? roomAttributes.Value.Count(attrs => attrs[category - 1] >= threshold) : 0;
        public int FurnitureOfQuality(int quality, bool onlyPlaced) =>
            qualities.Value.Count(piece => piece.Quality == quality && (!onlyPlaced || piece.Placed));
        public int SetOwned(int suitId) => sets.Value[suitId].Count();

        public int HighMoodDays(int required, int minimum, long now) => dorm.Characters
            .Where(character => character.HighMoodSince > 0 && character.HighMoodSince <= now
                && character.Mood >= minimum * 100 && dorm.Rooms.Any(room => room.Id == character.DormitoryId))
            .Select(character => checked((int)((now - character.HighMoodSince) / 86400)))
            .OrderDescending().Skip(required - 1).FirstOrDefault();

        public int SetPlaced(int templateId)
        {
            if (!DormTemplateRequirements.Value.TryGetValue(templateId, out var required))
                return 0;
            // Each template describes one room. Repeated furniture ids require distinct copies in that room.
            return placed.Value.Select(room => room.GroupBy(piece => piece.ConfigId)
                    .Sum(group => Math.Min(group.Count(), required.GetValueOrDefault(group.Key))))
                .DefaultIfEmpty(0).Max();
        }
    }
}
