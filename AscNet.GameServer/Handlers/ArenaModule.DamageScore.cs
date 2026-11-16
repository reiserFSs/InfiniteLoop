using System.Globalization;
using System.Text;
using AscNet.GameServer.Commands;
using AscNet.Table.V2.share.fuben.arena;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    // /warzone dmg: per-install tuning of the damage part of the Warzone score (Mark.tsv EnemyHpPoint), as a percent of
    // the repository expression (simulated_battlefield.json ArenaMarkOverrides, else Mark.tsv). 100 = default.
    internal partial class ArenaModule
    {
        internal static string DamageScoreFromCommand(IReadOnlyList<string> terms)
        {
            string usage = "Usage: /warzone dmg [percent|reset] [boss|duo|mob|MarkId]. 100 = default, 120 = +20% damage points, 50 = half. "
                + "Without a mode it applies to every damage-scored mode. Examples: /warzone dmg 120 boss, /warzone dmg reset";
            List<int> damageMarks = DamageMarkIds();
            if (terms.Count == 0)
                return DescribeDamageScore(damageMarks);
            if (terms.Count > 2)
                throw new CommandMessageCallbackException(usage);

            List<int>? selected = terms.Count == 2 ? [ResolveDamageMark(terms[1], damageMarks)] : null;
            List<int> targets = selected ?? damageMarks;
            if (terms[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                ArenaLocalScoreSettings.ResetDamagePercent(selected);
                return $"Damage score reset to default for {MarkList(targets)}.\n{DescribeDamageScore(damageMarks)}";
            }

            if (!int.TryParse(terms[0].TrimEnd('%'), NumberStyles.None, CultureInfo.InvariantCulture, out int percent)
                || percent < ArenaLocalScoreSettings.MinPercent || percent > ArenaLocalScoreSettings.MaxPercent)
                throw new CommandMessageCallbackException(
                    $"Percent must be a whole number from {ArenaLocalScoreSettings.MinPercent} to {ArenaLocalScoreSettings.MaxPercent}. {usage}");
            ArenaLocalScoreSettings.SetDamagePercent(targets, percent);
            return $"Damage score set to {percent}% for {MarkList(targets)}. New fights only; existing best scores are kept.\n{DescribeDamageScore(damageMarks)}";
        }

        // Marks whose damage expression reads EnemyHp (Mark 1/2 score the kill ratio instead).
        private static List<int> DamageMarkIds() => Marks.Value.Values
            .Where(mark => RepositoryMarkExpression(mark.MarkId, "EnemyHpPoint", mark.EnemyHpPoint)?.Contains("EnemyHp", StringComparison.Ordinal) == true)
            .Select(mark => mark.MarkId)
            .Order()
            .ToList();

        private static int ResolveDamageMark(string term, List<int> damageMarks)
        {
            int markId;
            if (int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                markId = parsed;
            else
            {
                ZoneEncounterType? type = Catalog.Value.EncounterTypes.FirstOrDefault(candidate =>
                    candidate.Name.Equals(term, StringComparison.OrdinalIgnoreCase)
                    || candidate.Aliases.Any(alias => alias.Equals(term, StringComparison.OrdinalIgnoreCase)));
                List<int> typeMarks = type is null ? [] : damageMarks.Where(mark => MarkTypes(mark).Contains(type.Name)).ToList();
                if (typeMarks.Count != 1)
                    throw new CommandMessageCallbackException($"Unknown mode \"{term}\". Use one of: {MarkList(damageMarks)}.");
                markId = typeMarks[0];
            }
            if (!damageMarks.Contains(markId))
                throw new CommandMessageCallbackException(Marks.Value.ContainsKey(markId)
                    ? $"Mark {markId} is not scored by damage. Damage-scored modes: {MarkList(damageMarks)}."
                    : $"Unknown MarkId {markId}. Damage-scored modes: {MarkList(damageMarks)}.");
            return markId;
        }

        private static IReadOnlyList<string> MarkTypes(int markId) => AreaStages.Value.Values
            .Where(area => area.MarkId == markId)
            .Select(ZoneType)
            .Where(type => type != "Other")
            .Distinct()
            .ToList();

        private static string MarkLabel(int markId)
        {
            IReadOnlyList<string> types = MarkTypes(markId);
            return types.Count == 0 ? $"Mark {markId}" : $"{string.Join("/", types)} (Mark {markId})";
        }

        private static string MarkList(IEnumerable<int> markIds) => string.Join(", ", markIds.Select(MarkLabel));

        private static string DescribeDamageScore(List<int> damageMarks)
        {
            StringBuilder message = new("Damage score (points for 1M damage):");
            foreach (int markId in damageMarks)
            {
                MarkTable mark = Marks.Value[markId];
                int? percent = ArenaLocalScoreSettings.DamagePercent(markId);
                string? expression = MarkExpression(markId, "EnemyHpPoint", mark.EnemyHpPoint);
                int points = Evaluate(expression, new Dictionary<string, double> { ["EnemyHp"] = 1_000_000 }, mark.MaxEnemyHpPoint);
                message.Append($"\n- {MarkLabel(markId)}: {percent ?? 100}%{(percent is null ? " (default)" : string.Empty)}, "
                    + $"{expression}, {points.ToString("N0", CultureInfo.InvariantCulture)} pts");
            }
            message.Append($"\nLocal file (not in git): {ArenaLocalScoreSettings.FilePath}");
            if (ArenaLocalScoreSettings.LoadError is { } error)
                message.Append($"\nWARNING: the local file could not be read and is ignored: {error}");
            return message.ToString();
        }
    }

    // Local, per-install settings written by /warzone dmg. The file sits next to simulated_battlefield.json in the
    // source Resources/Configs folder; Resources/Configs/*.local.json is gitignored and excluded from the build copy, so
    // each user keeps their own values and a pull never changes them. No file or no entry => repository default.
    internal static class ArenaLocalScoreSettings
    {
        internal const string FileName = "simulated_battlefield.local.json";
        internal const int MinPercent = 1;
        internal const int MaxPercent = 1000;
        private const string DamagePercentKey = "DamagePercent";
        private const string Comment = "Local AscNet settings (gitignored, per install). Managed by /warzone dmg; delete this file to restore the defaults.";
        private static readonly object Sync = new();
        private static readonly Lazy<string> DefaultPath = new(ResolveDefaultPath);
        private static string? pathOverride;

        // Tests point this at a temporary file so they never read or write the real local settings.
        internal static string? PathOverride
        {
            get => pathOverride;
            set { lock (Sync) pathOverride = value; }
        }

        internal static string FilePath => PathOverride ?? DefaultPath.Value;

        internal static string? LoadError
        {
            get
            {
                _ = Load(out string? error);
                return error;
            }
        }

        internal static int? DamagePercent(int markId)
        {
            JToken? value = Load(out _)[DamagePercentKey]?[markId.ToString(CultureInfo.InvariantCulture)];
            return value?.Type == JTokenType.Integer && value.Value<long>() is >= MinPercent and <= MaxPercent
                ? value.Value<int>()
                : null;
        }

        internal static void SetDamagePercent(IEnumerable<int> markIds, int percent)
        {
            lock (Sync)
            {
                JObject settings = LoadForWrite();
                JObject percents = settings[DamagePercentKey] as JObject ?? new JObject();
                foreach (int markId in markIds)
                {
                    if (percent == 100)
                        percents.Remove(markId.ToString(CultureInfo.InvariantCulture));
                    else
                        percents[markId.ToString(CultureInfo.InvariantCulture)] = percent;
                }
                settings[DamagePercentKey] = percents;
                Save(settings);
            }
        }

        // null => every mark.
        internal static void ResetDamagePercent(IEnumerable<int>? markIds)
        {
            lock (Sync)
            {
                if (markIds is null)
                {
                    if (!File.Exists(FilePath))
                        return;
                    JObject settings = Load(out string? error);
                    if (error is not null)
                    {
                        File.Delete(FilePath); // An unreadable file is ignored anyway; a full reset removes it.
                        return;
                    }
                    settings.Remove(DamagePercentKey);
                    Save(settings);
                    return;
                }
                JObject current = LoadForWrite();
                if (current[DamagePercentKey] is JObject percents)
                    foreach (int markId in markIds)
                        percents.Remove(markId.ToString(CultureInfo.InvariantCulture));
                Save(current);
            }
        }

        private static JObject Load(out string? error)
        {
            error = null;
            lock (Sync)
            {
                string path = FilePath;
                if (!File.Exists(path))
                    return new JObject();
                try
                {
                    return JObject.Parse(File.ReadAllText(path));
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    error = $"{path}: {exception.Message}";
                    return new JObject();
                }
            }
        }

        private static JObject LoadForWrite()
        {
            JObject settings = Load(out string? error);
            if (error is not null)
                throw new CommandMessageCallbackException($"The local settings file is not valid JSON; fix or delete it first. {error}");
            return settings;
        }

        private static void Save(JObject settings)
        {
            if (settings[DamagePercentKey] is JObject { HasValues: false })
                settings.Remove(DamagePercentKey);
            string path = FilePath;
            if (!settings.Properties().Any(property => property.Name != "_comment"))
            {
                File.Delete(path);
                return;
            }
            settings["_comment"] = Comment;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, settings.ToString(Formatting.Indented) + Environment.NewLine);
        }

        // The source Resources/Configs folder (found upward from the working and base directories), so the file is the
        // same whether the server runs from the project folder or from bin/; a standalone build falls back to bin/Configs.
        private static string ResolveDefaultPath()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                {
                    string configs = Path.Combine(directory.FullName, "Resources", "Configs");
                    if (File.Exists(Path.Combine(configs, "simulated_battlefield.json")))
                        return Path.Combine(configs, FileName);
                }
            }
            return Path.Combine(AppContext.BaseDirectory, "Configs", FileName);
        }
    }
}
