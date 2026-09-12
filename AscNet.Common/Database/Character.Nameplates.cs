using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.nameplate;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Character
{
    private static readonly Lazy<Dictionary<int, NameplateTable>> NameplateCatalog = new(() =>
        TableReaderV2.Parse<NameplateTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, NameplateTable[]>> NameplateGroups = new(() =>
        NameplateCatalog.Value.Values.GroupBy(row => row.Group).ToDictionary(
            group => group.Key, group => group.OrderBy(row => row.NameplateQuality).ToArray()));

    [BsonElement("nameplates")]
    public List<NameplateData> Nameplates { get; set; } = [];

    [BsonElement("current_wear_nameplate")]
    public int CurrentWearNameplate { get; set; }

    public static NameplateTable? GetNameplateConfig(int id) => NameplateCatalog.Value.GetValueOrDefault(id);

    public int GetCurrentWearNameplate(long now) => Nameplates.Any(value =>
        value.Id == CurrentWearNameplate && GetNameplateConfig(value.Id) is not null
        && (value.EndTime == 0 || value.EndTime > now)) ? CurrentWearNameplate : 0;

    public NotifyNameplateLoginData BuildNameplateLoginData() => new()
    {
        CurrentWearNameplate = GetCurrentWearNameplate(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        UnlockNameplates = Nameplates.Where(value => GetNameplateConfig(value.Id) is not null).Cast<dynamic>().ToList()
    };

    public NameplateData GrantNameplate(int id, int count, long now)
    {
        NameplateTable? config = GetNameplateConfig(id);
        // TypeTwo Params describe MoeWar vote promotion; an explicit reward already selects its tier.
        if (count <= 0 || config is null || config.NameplateUpgradeType is not (1 or 2 or 3)
            || config.Duration < 0 || config.PlusTime != 0
            || config.ConvertItemId != 0 || config.ConvertItemCount != 0)
            throw new InvalidDataException($"Unsupported nameplate reward {id}.");

        NameplateData? owned = Nameplates.FirstOrDefault(value =>
            GetNameplateConfig(value.Id)?.Group == config.Group);
        int previousId = owned?.Id ?? 0;
        bool expired = owned is not null && owned.EndTime != 0 && owned.EndTime <= now;
        if (expired)
        {
            Nameplates.Remove(owned!);
            if (CurrentWearNameplate == previousId)
                CurrentWearNameplate = 0;
            owned = null;
        }

        NameplateTable selected = owned is null ? config : GetNameplateConfig(owned.Id)!;
        int duplicateCount = count;
        if (owned is null)
        {
            owned = new NameplateData { Id = id, GetTime = now };
            Nameplates.Add(owned);
            duplicateCount--;
        }
        else if (config.NameplateQuality > selected.NameplateQuality)
        {
            selected = config;
            owned.Exp = 0;
            duplicateCount--;
        }

        // User-approved local policy: consume authored EXP thresholds in group quality order.
        // A fresh/expired acquisition starts at its awarded tier; only extra copies become EXP.
        // An explicitly higher-tier award replaces the tier before processing its extra copies.
        if (config.NameplateUpgradeType == 3)
        {
            if (config.DecomposeExp <= 0)
                throw new InvalidDataException($"Invalid nameplate EXP reward {id}.");
            int exp = checked(owned.Exp + checked(config.DecomposeExp * duplicateCount));
            NameplateTable[] group = NameplateGroups.Value[config.Group];
            int index = Array.FindIndex(group, row => row.Id == selected.Id);
            while (index + 1 < group.Length && selected.UpgradeExp > 0 && exp >= selected.UpgradeExp)
            {
                exp -= selected.UpgradeExp;
                selected = group[++index];
            }
            owned.Exp = index + 1 == group.Length ? 0 : exp;
        }
        else
        {
            owned.Exp = 0;
        }

        // User-approved local timer policy: refresh from the durable grant clock, never shorten
        // an active expiry. Expired records reset their tier/EXP/GetTime instead of carrying over.
        long endTime = selected.Duration == 0 ? 0 : checked(now + selected.Duration);
        if (expired || previousId == 0)
            owned.EndTime = endTime;
        else if (owned.EndTime != 0)
            owned.EndTime = endTime == 0 ? 0 : Math.Max(owned.EndTime, endTime);
        owned.Id = selected.Id;
        if (CurrentWearNameplate == previousId && previousId != 0)
            CurrentWearNameplate = owned.Id;
        return owned;
    }
}
