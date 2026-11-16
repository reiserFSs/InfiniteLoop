using System.Text;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Commands;
using AscNet.Table.V2.share.fuben.arena;
using Newtonsoft.Json;

namespace AscNet.GameServer.Handlers
{
    // /warzone zones and /warzone set: find Warzone zones by element (zone buff), zone name and encounter type.
    // Zones, buffs, Desc and MarkId come from AreaStage.tsv; Configs/warzone_zone_catalog.json only supplies the
    // element keyword of each zone buff id and the encounter type of each Desc prefix (AscNet command policy).
    internal partial class ArenaModule
    {
        private const string ZoneCatalogPath = "Configs/warzone_zone_catalog.json";
        private const int MaxListedZones = 40;
        private const int MaxListedAlternatives = 8;
        private static readonly Lazy<ZoneCatalog> Catalog = new(LoadZoneCatalog);

        internal static string ListZonesForCommand(Session session, IReadOnlyList<string> filterTerms)
        {
            ReconcileLive(session);
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield!;
            ChallengeAreaTable challenge = ResolveChallenge(state.ArenaLevel, checked((int)session.player.PlayerData.Level));
            HashSet<int> groupedAreaIds = GroupedAreaIds(challenge);
            ZoneQuery? query = filterTerms.Count == 0 ? null : ParseZoneQuery(filterTerms);
            List<AreaStageTable> zones = GetZoneCandidates(challenge)
                .Select(areaId => AreaStages.Value[areaId])
                .Where(area => query is null || query.Matches(area) && (query.Type is null || ZoneType(area) == query.Type))
                .OrderBy(area => groupedAreaIds.Contains(area.Id) ? 0 : 1)
                .ThenBy(area => area.Id)
                .ToList();
            // Unfiltered: list the tier's regular zones and summarize the (large) Entropic Distortion slot.
            List<AreaStageTable> listed = query is null ? zones.Where(area => groupedAreaIds.Contains(area.Id)).ToList() : zones;
            if (zones.Count == 0)
                return query is null
                    ? "No zones are available for your current Warzone tier."
                    : ExplainNoZone(query, challenge, groupedAreaIds);

            string filter = query is null ? "" : $" matching {(query.Type is null ? query.Label : $"{query.Label} {query.Type}")}";
            StringBuilder message = new($"Warzone zones for tier {TierName(challenge.ArenaLv)}{filter} (AreaId Name [Element] Type):");
            foreach (AreaStageTable area in listed.Take(MaxListedZones))
                message.Append('\n').Append(FormatZone(area, state.ArenaZoneOverrideAreaId));
            if (listed.Count > MaxListedZones)
                message.Append($"\n... {listed.Count - MaxListedZones} more; narrow with /warzone zones <element> [boss|duo|mob].");
            int specialCount = zones.Count - listed.Count;
            if (specialCount > 0)
                message.Append($"\nEntropic Distortion slot: {specialCount} zones; list them with /warzone zones <element> [boss|duo|mob].");
            message.Append($"\nActive zones: {string.Join(", ", ActiveZones(session, challenge))}. Use /warzone set <element|zone name> [boss|duo|mob] or /warzone zone <AreaId|auto>.");
            return message.ToString();
        }

        internal static string SetZoneByQueryFromCommand(Session session, IReadOnlyList<string> terms)
        {
            if (terms.Count == 0)
                throw new CommandMessageCallbackException("Usage: /warzone set <element|zone name|AreaId> [boss|duo|mob]. Elements: "
                    + string.Join(", ", Catalog.Value.Elements.Select(element => element.Aliases.FirstOrDefault() ?? Normalize(element.Name))) + ".");
            ReconcileLive(session);
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield!;
            ChallengeAreaTable challenge = ResolveChallenge(state.ArenaLevel, checked((int)session.player.PlayerData.Level));
            HashSet<int> groupedAreaIds = GroupedAreaIds(challenge);

            if (terms.Count == 1 && (terms[0].Equals("auto", StringComparison.OrdinalIgnoreCase) || terms[0] == "0"))
            {
                SetZoneFromCommand(session, 0);
                return $"Warzone zone set to auto. Active zones: {string.Join(", ", ActiveZones(session, challenge))}.";
            }
            if (terms.Count == 1 && int.TryParse(terms[0], out int requestedAreaId))
            {
                SetZoneFromCommand(session, requestedAreaId);
                return SetZoneMessage(session, challenge, AreaStages.Value[requestedAreaId], null, []);
            }

            ZoneQuery query = ParseZoneQuery(terms);
            List<AreaStageTable> matches = GetZoneCandidates(challenge)
                .Select(areaId => AreaStages.Value[areaId])
                .Where(query.Matches)
                .ToList();
            IEnumerable<string> typePreference = query.Type is { } requestedType
                ? [requestedType]
                : Catalog.Value.EncounterTypes.Select(type => type.Name);
            foreach (string type in typePreference)
            {
                // Preference (AscNet policy): the element as the zone's main buff, then the tier's regular zones
                // before Entropic Distortion, then the lowest AreaId.
                List<AreaStageTable> ranked = matches
                    .Where(area => ZoneType(area) == type)
                    .OrderByDescending(area => query.PrimaryBuffId is int buffId && area.BuffIds.FirstOrDefault() == buffId)
                    .ThenBy(area => groupedAreaIds.Contains(area.Id) ? 0 : 1)
                    .ThenBy(area => area.Id)
                    .ToList();
                if (ranked.Count == 0)
                    continue;

                SetZoneFromCommand(session, ranked[0].Id);
                string? fallback = query.Type is null && type != Catalog.Value.EncounterTypes[0].Name
                    ? $"No {Catalog.Value.EncounterTypes[0].Name} zone matching {query.Label} is available; picked {type}."
                    : null;
                return SetZoneMessage(session, challenge, ranked[0], fallback, ranked.Skip(1).Select(area => area.Id).ToList());
            }

            throw new CommandMessageCallbackException(ExplainNoZone(query, challenge, groupedAreaIds));
        }

        private static string SetZoneMessage(Session session, ChallengeAreaTable challenge, AreaStageTable area, string? fallback, IReadOnlyList<int> alternatives)
        {
            StringBuilder message = new();
            if (fallback is not null)
                message.Append(fallback).Append('\n');
            message.Append($"Warzone zone set to {FormatZone(area, 0)} (MarkId {area.MarkId}).");
            if (ResolveConfiguredStageId(area) is { } stageId
                && ArenaStageDataset.DescribeProfile(area.Id, checked((uint)stageId), area.MarkId, area.Desc) is { } profile)
                message.Append($"\nEnemies: {profile}.");
            message.Append($"\nActive zones: {string.Join(", ", ActiveZones(session, challenge))}.");
            if (alternatives.Count > 0)
                message.Append($"\nOther matches: {string.Join(", ", alternatives.Take(MaxListedAlternatives))}{(alternatives.Count > MaxListedAlternatives ? ", ..." : "")} (use /warzone zone <AreaId>).");
            message.Append("\nScore was reset; re-enter Warzone to load the zone.");
            return message.ToString();
        }

        private static string ExplainNoZone(ZoneQuery query, ChallengeAreaTable challenge, HashSet<int> groupedAreaIds)
        {
            string subject = query.Type is null ? query.Label : $"{query.Label} {query.Type}";
            List<AreaStageTable> tableMatches = AreaStages.Value.Values
                .Where(area => area.IsAbandoned == 0 && query.Matches(area) && (query.Type is null || ZoneType(area) == query.Type))
                .ToList();
            if (tableMatches.Count == 0)
                return $"No Warzone zone in AreaStage matches {subject}.";
            List<AreaStageTable> hydratable = tableMatches.Where(IsSupportedArea).ToList();
            if (hydratable.Count == 0)
            {
                string supportedTypes = string.Join(", ", AreaStages.Value.Values.Where(IsSupportedArea).Select(ZoneType).Distinct());
                return $"No enemy profile in arena_stage_data.json for {string.Join("/", tableMatches.Select(ZoneType).Distinct())} zones "
                    + $"(MarkId {string.Join("/", tableMatches.Select(area => area.MarkId).Distinct())}) matching {query.Label}. Zone types with enemy data: {supportedTypes}.";
            }
            if (!IsHeroOrHigher(challenge.ArenaLv) && hydratable.All(area => !groupedAreaIds.Contains(area.Id)))
            {
                string heroTier = TierName(RequiredInt(ActivityConfig, "HeroArenaLevel"));
                return $"{subject} zones exist only in the Entropic Distortion slot, which opens at tier {heroTier}. Use /warzone tier {heroTier} first.";
            }
            return $"No {subject} zone is available for your current Warzone tier {TierName(challenge.ArenaLv)}.";
        }

        private static List<int> ActiveZones(Session session, ChallengeAreaTable challenge)
        {
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield!;
            return SelectedAreas(state.ArenaActivityNo, session.player.PlayerData.Id, challenge, state.ArenaZoneOverrideAreaId);
        }

        private static string FormatZone(AreaStageTable area, int forcedAreaId) =>
            $"{area.Id} {area.Name} [{string.Join("+", ZoneElements(area))}] {ZoneType(area)}{(area.Id == forcedAreaId ? " (selected)" : "")}";

        private static IReadOnlyList<string> ZoneElements(AreaStageTable area) => area.BuffIds
            .Where(buffId => buffId > 0)
            .Select(buffId => Catalog.Value.ElementByBuffId.TryGetValue(buffId, out ZoneElement? element) ? element.Name : $"Buff {buffId}")
            .ToList();

        private static string ZoneType(AreaStageTable area) => Catalog.Value.EncounterTypes
            .FirstOrDefault(type => type.DescPrefixes.Any(prefix => area.Desc.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))?.Name
            ?? "Other";

        private static string TierName(int arenaLevel) =>
            TableReaderV2.Parse<ArenaLevelTable>().FirstOrDefault(level => level.Id == arenaLevel)?.Name ?? arenaLevel.ToString();

        // Query = [element alias | AreaStage.Name | AreaStage.BuffName] [type alias]; the type is optional and last.
        private static ZoneQuery ParseZoneQuery(IReadOnlyList<string> terms)
        {
            List<string> words = terms.Where(term => !string.IsNullOrWhiteSpace(term)).ToList();
            string? type = null;
            if (words.Count > 0 && Catalog.Value.TypeByAlias.TryGetValue(Normalize(words[^1]), out string? lastType))
            {
                type = lastType;
                words.RemoveAt(words.Count - 1);
            }
            if (words.Count == 0)
                return new ZoneQuery("any", _ => true, null, type);

            string phrase = string.Join(" ", words);
            string key = Normalize(phrase);
            if (Catalog.Value.ElementByAlias.TryGetValue(key, out ZoneElement? element))
                return new ZoneQuery(element.Name, area => area.BuffIds.Contains(element.BuffId), element.BuffId, type);

            AreaStageTable? named = AreaStages.Value.Values.FirstOrDefault(area => Normalize(area.Name) == key);
            if (named is not null)
                return new ZoneQuery(named.Name, area => Normalize(area.Name) == key, null, type);

            List<(string Name, int BuffId)> buffs = AreaStages.Value.Values
                .SelectMany(area => area.BuffName.Zip(area.BuffIds, (name, buffId) => (Name: name, BuffId: buffId)))
                .Where(buff => buff.BuffId > 0 && !string.IsNullOrWhiteSpace(buff.Name) && Normalize(buff.Name) == key)
                .ToList();
            if (buffs.Select(buff => buff.BuffId).Distinct().Count() == 1)
            {
                int buffId = buffs[0].BuffId;
                string label = Catalog.Value.ElementByBuffId.TryGetValue(buffId, out ZoneElement? buffElement)
                    ? $"{buffs[0].Name} ({buffElement.Name})"
                    : buffs[0].Name;
                return new ZoneQuery(label, area => area.BuffIds.Contains(buffId), buffId, type);
            }

            throw new CommandMessageCallbackException($"Unknown Warzone element or zone name '{phrase}'. Elements: "
                + string.Join(", ", Catalog.Value.Elements.Select(candidate => candidate.Name))
                + ". Types: " + string.Join(", ", Catalog.Value.EncounterTypes.Select(candidate => candidate.Name.ToLowerInvariant())) + ".");
        }

        private static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static ZoneCatalog LoadZoneCatalog()
        {
            string json = JsonSnapshot.LoadText(ZoneCatalogPath);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException($"{ZoneCatalogPath} is missing.");
            ZoneCatalog catalog = JsonConvert.DeserializeObject<ZoneCatalog>(json)
                ?? throw new InvalidDataException($"Could not deserialize {ZoneCatalogPath}.");
            if (catalog.Elements.Count == 0 || catalog.EncounterTypes.Count == 0)
                throw new InvalidDataException($"{ZoneCatalogPath}: Elements and EncounterTypes are required.");
            if (catalog.Elements.Select(element => element.BuffId).Distinct().Count() != catalog.Elements.Count)
                throw new InvalidDataException($"{ZoneCatalogPath}: element BuffIds must be unique.");
            if (catalog.EncounterTypes.Any(type => type.DescPrefixes.Count == 0))
                throw new InvalidDataException($"{ZoneCatalogPath}: every encounter type needs a Desc prefix.");

            Dictionary<string, string> owners = new();
            void Claim(string alias, string owner)
            {
                string key = Normalize(alias);
                if (key.Length == 0 || (owners.TryGetValue(key, out string? existing) && existing != owner))
                    throw new InvalidDataException($"{ZoneCatalogPath}: alias '{alias}' is empty or ambiguous.");
                owners[key] = owner;
            }
            foreach (ZoneElement element in catalog.Elements)
            {
                foreach (string alias in element.Aliases.Append(element.Name))
                {
                    Claim(alias, $"element:{element.BuffId}");
                    catalog.ElementByAlias[Normalize(alias)] = element;
                }
                catalog.ElementByBuffId[element.BuffId] = element;
            }
            foreach (ZoneEncounterType type in catalog.EncounterTypes)
            {
                foreach (string alias in type.Aliases.Append(type.Name))
                {
                    Claim(alias, $"type:{type.Name}");
                    catalog.TypeByAlias[Normalize(alias)] = type.Name;
                }
            }
            return catalog;
        }

        private sealed record ZoneQuery(string Label, Func<AreaStageTable, bool> Matches, int? PrimaryBuffId, string? Type);

        private sealed class ZoneCatalog
        {
            public List<ZoneElement> Elements { get; set; } = new();
            public List<ZoneEncounterType> EncounterTypes { get; set; } = new();
            [JsonIgnore] public Dictionary<string, ZoneElement> ElementByAlias { get; } = new();
            [JsonIgnore] public Dictionary<int, ZoneElement> ElementByBuffId { get; } = new();
            [JsonIgnore] public Dictionary<string, string> TypeByAlias { get; } = new();
        }

        private sealed class ZoneElement
        {
            public int BuffId { get; set; }
            public string Name { get; set; } = string.Empty;
            public List<string> Aliases { get; set; } = new();
        }

        private sealed class ZoneEncounterType
        {
            public string Name { get; set; } = string.Empty;
            public List<string> Aliases { get; set; } = new();
            public List<string> DescPrefixes { get; set; } = new();
        }
    }
}
