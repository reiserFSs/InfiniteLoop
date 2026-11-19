using System.Reflection;
using System.Text.RegularExpressions;
using AscNet.GameServer.Commands;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        // Player-facing commands that the AscNet Menu deliberately does not expose. Every other [CommandName] must be in the spec.
        private static readonly Dictionary<string, string> GmPanelSkippedCommands = new()
        {
            ["wz"] = "alias of /warzone (same class, same arguments)",
        };

        private static readonly string[] GmPanelLanguages = ["en", "zh-TW", "ko", "ja", "zh-CN"];

        // --gm-panel-spec-only: Mods/ascnet-menu/XAscNet/XAscNetMenuSpec.lua (the JSON in its long string) against the
        // server's Command attributes: command names, sub-actions, argument names/positions/patterns/optionality, and that
        // the lines the menu would send pass the server's own argument validation. No session, no database.
        private static void ValidateGmPanelSpec()
        {
            CommandFactory.commands.Clear();
            CommandFactory.LoadCommands();
            string root = new Func<string>(() =>
            {
                for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "AscNet.sln")))
                        return dir.FullName;
                throw new DirectoryNotFoundException("AscNet.sln not found above the test binary.");
            })();
            string lua = File.ReadAllText(Path.Combine(root, "Mods", "ascnet-menu", "XAscNet", "XAscNetMenuSpec.lua"));
            int start = lua.IndexOf("[==[", StringComparison.Ordinal) + 4, end = lua.IndexOf("]==]", StringComparison.Ordinal);
            JObject spec = JObject.Parse(lua[start..end]);
            JObject text = (JObject)spec["text"]!;

            List<string> errors = [];
            void Fail(string message) => errors.Add(message);
            void NeedText(string? key, string where)
            {
                if (key is null || text[key] is not JObject entry) { Fail($"{where}: text key '{key}' is missing"); return; }
                foreach (string language in GmPanelLanguages)
                    if (string.IsNullOrWhiteSpace((string?)entry[language]))
                        Fail($"{where}: text key '{key}' has no {language} text");
                if (entry.Properties().Select(p => p.Name).Except(GmPanelLanguages).Any())
                    Fail($"{where}: text key '{key}' has an unknown language");
            }
            foreach (JProperty property in text.Properties())
            {
                // string.Format placeholders must agree across languages.
                int[] counts = GmPanelLanguages.Select(language => Regex.Matches((string?)property.Value[language] ?? "", "%s").Count).Distinct().ToArray();
                if (counts.Length > 1) Fail($"text key '{property.Name}': %s count differs between languages");
            }

            HashSet<string> exposed = [];
            HashSet<string> groupIds = [];
            foreach (JObject group in spec["groups"]!)
            {
                string groupId = (string)group["id"]!;
                if (!groupIds.Add(groupId)) Fail($"group '{groupId}' is listed twice");
                NeedText($"g.{groupId}", $"group {groupId}");
                foreach (JObject command in group["commands"]!)
                {
                    string name = (string)command["cmd"]!;
                    string at = $"/{name}";
                    if (!exposed.Add(name)) Fail($"{at} is listed twice");
                    NeedText($"c.{name}", at);
                    NeedText($"c.{name}.help", at);
                    if (GmPanelSkippedCommands.ContainsKey(name)) Fail($"{at} is both in the spec and on the skip list");
                    if (!CommandFactory.commands.TryGetValue(name, out Type? type)) { Fail($"{at} is not a server command"); continue; }

                    Dictionary<string, ArgumentAttribute> arguments = type
                        .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Select(property => (property.Name, Attribute: property.GetCustomAttribute<ArgumentAttribute>()))
                        .Where(entry => entry.Attribute is not null)
                        .ToDictionary(entry => entry.Name, entry => entry.Attribute!);
                    HashSet<string> actionIds = [];
                    HashSet<string> firstLiterals = [];
                    bool everyActionStartsWithLiteral = true;

                    foreach (JObject action in command["actions"]!)
                    {
                        string id = (string)action["id"]!;
                        string where = $"{at} action '{id}'";
                        if (!actionIds.Add(id)) Fail($"{where} is listed twice");
                        NeedText($"a.{name}.{id}", where);
                        if (action["confirm"] is not null) NeedText((string?)action["confirm"], where);
                        string[] line = action["line"]!.Select(token => (string)token!).ToArray();
                        if (line.Length > 0 && !line[0].StartsWith('{')) firstLiterals.Add(line[0]); else everyActionStartsWithLiteral = false;
                        Dictionary<string, JObject> fields = (action["fields"] ?? new JArray()).Cast<JObject>().ToDictionary(field => (string)field["id"]!);
                        HashSet<string> placeholders = line.Where(token => token.StartsWith('{')).Select(token => token[1..^1]).ToHashSet();
                        foreach (string missing in placeholders.Except(fields.Keys)) Fail($"{where}: line uses {{{missing}}} but there is no such field");
                        foreach (string unused in fields.Keys.Except(placeholders)) Fail($"{where}: field '{unused}' is not used by the line");

                        // Each case is a sample value set; the server must accept the resulting arguments.
                        List<Dictionary<string, string>> cases = [fields.ToDictionary(f => f.Key, f => (string)f.Value["sample"]!)];
                        cases.Add(fields.Where(f => f.Value["optional"]?.Value<bool>() != true).ToDictionary(f => f.Key, f => (string)f.Value["sample"]!));
                        foreach ((string fieldId, JObject field) in fields)
                        {
                            string fieldWhere = $"{where} field '{fieldId}'";
                            string kind = (string)field["kind"]!;
                            if (kind is not ("number" or "text" or "choice")) Fail($"{fieldWhere}: unknown kind '{kind}'");
                            NeedText((string?)field["label"], fieldWhere);
                            if (field["hint"] is not null) NeedText((string?)field["hint"], fieldWhere);
                            string argName = (string)field["arg"]!;
                            if (!arguments.TryGetValue(argName, out ArgumentAttribute? attribute))
                            {
                                Fail($"{fieldWhere}: the server has no argument '{argName}'");
                                continue;
                            }
                            int index = Array.IndexOf(line, $"{{{fieldId}}}");
                            if (attribute.Position != index) Fail($"{fieldWhere}: '{argName}' is argument {attribute.Position} on the server, the line puts it at {index}");
                            if ((string?)field["regex"] != attribute.Pattern.ToString()) Fail($"{fieldWhere}: regex differs from the server's '{attribute.Pattern}'");
                            if (field["optional"]?.Value<bool>() == true && (attribute.Flags & ArgumentFlags.Optional) == 0) Fail($"{fieldWhere}: optional here, required on the server");
                            string sample = (string)field["sample"]!;
                            if (kind == "number" && !Regex.IsMatch(sample, "^[0-9]+$")) Fail($"{fieldWhere}: number sample '{sample}' is not digits");
                            IEnumerable<string> alternatives = ((field["choices"] as JArray) ?? []).Concat(field["allow"] as JArray ?? []).Select(v => (string)v!);
                            if (kind == "choice" && field["choices"] is not JArray) Fail($"{fieldWhere}: choice without choices");
                            // War Zone encounter types: the menu offers exactly the types the server has enemy data for.
                            if (kind == "choice" && fieldId == "type" && where.Contains("warzone", StringComparison.Ordinal))
                            {
                                string available = (string)RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ArenaModule"), "TypeChoices",
                                    BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)]).Invoke(null, ["|"])!;
                                if (string.Join("|", alternatives) != available) Fail($"{fieldWhere}: choices '{string.Join("|", alternatives)}' differ from the server's encounter types with enemy data '{available}'");
                            }
                            foreach (string alternative in alternatives)
                            {
                                if (!attribute.Pattern.IsMatch(alternative)) Fail($"{fieldWhere}: '{alternative}' does not match the server's '{attribute.Pattern}'");
                                cases.Add(fields.ToDictionary(f => f.Key, f => f.Key == fieldId ? alternative : (string)f.Value["sample"]!));
                            }
                        }

                        foreach (Dictionary<string, string> values in cases)
                        {
                            List<string> args = [];
                            foreach (string token in line)
                            {
                                if (!token.StartsWith('{')) { args.Add(token); continue; }
                                args.AddRange(values.GetValueOrDefault(token[1..^1], "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
                            }
                            try { CommandFactory.CreateCommand(name, null!, args.ToArray()); }
                            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException invalid)
                            {
                                Fail($"{where}: the server rejects '/{name} {string.Join(' ', args)}': {invalid.Message}");
                            }
                        }
                    }

                    // Sub-actions: an alternative the server's first argument accepts, or a "/<name> <word>" its Help mentions, must be exposed.
                    HashSet<string> serverActions = [];
                    if (everyActionStartsWithLiteral && arguments.Values.FirstOrDefault(a => a.Position == 0) is { } first)
                    {
                        string[] words = Regex.Replace(first.Pattern.ToString(), @"^\^?\(?|\)?\$?$", "").Split('|')
                            .Select(alternative => Regex.Replace(alternative, @"^\^|\$$", "")).ToArray();
                        if (words.All(word => Regex.IsMatch(word, "^[a-z]+$"))) serverActions.UnionWith(words);
                    }
                    string help = ((Command)CommandFactory.CreateCommand(name, null!, [], false)!).Help;
                    foreach (Match match in Regex.Matches(help, $@"/{Regex.Escape(name)} ([a-z]+)"))
                        serverActions.Add(match.Groups[1].Value);
                    foreach (string action in serverActions.Except(firstLiterals))
                        Fail($"{at}: the server has sub-action '{action}' that the spec does not expose");
                }
            }

            foreach (string name in CommandFactory.commands.Keys.Except(exposed).Except(GmPanelSkippedCommands.Keys))
                Fail($"/{name} is a server command that is neither in the spec nor on the skip list");
            foreach (string name in GmPanelSkippedCommands.Keys.Except(CommandFactory.commands.Keys))
                Fail($"skip list entry /{name} is not a server command any more");

            if (errors.Count > 0)
                throw new InvalidDataException("AscNet Menu spec is out of sync with the server commands:\n  " + string.Join("\n  ", errors));
            Console.WriteLine($"AscNet Menu spec matches {exposed.Count} server commands ({GmPanelSkippedCommands.Count} skipped).");
        }
    }
}
