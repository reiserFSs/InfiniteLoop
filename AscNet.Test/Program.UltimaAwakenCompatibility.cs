using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.exhibition;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateUltimaAwakenCompatibility()
    {
        CharacterTable[] definitions = TableReaderV2.Parse<CharacterTable>().OrderBy(row => row.Id).ToArray();
        var mappings = TableReaderV2.Parse<CharacterSkillTable>().ToDictionary(row => row.CharacterId);
        var groups = TableReaderV2.Parse<CharacterSkillGroupTable>().ToDictionary(row => row.Id);
        var initialUpgrades = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
            .Where(row => row.Level == 0).ToDictionary(row => row.SkillId);
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        ExhibitionRewardTable[] rewards = TableReaderV2.Parse<ExhibitionRewardTable>().ToArray();
        var ultima = new Dictionary<int, (int GroupId, uint SkillId, ExhibitionRewardTable Reward)>();

        foreach (CharacterTable definition in definitions)
        {
            string name = $"Ultima {definition.Name}: {definition.TradeName} ({definition.Id})";
            AssertEqual(true, mappings.ContainsKey(definition.Id), $"{name} CharacterSkill mapping");
            int[] mappedGroups = mappings[definition.Id].SkillGroupId.Where(id => id > 0).Distinct().ToArray();
            foreach (int groupId in mappedGroups)
            {
                AssertEqual(true, groups.ContainsKey(groupId), $"{name} group {groupId} exists");
                int defaultSkillId = groups[groupId].SkillId.First(id => id > 0);
                if (!initialUpgrades.TryGetValue(defaultSkillId, out CharacterSkillUpgradeTable? initial))
                    continue;
                foreach (int conditionId in initial.ConditionId.Where(id => id > 0))
                {
                    ConditionTable condition = conditions[conditionId];
                    if (condition.Type != 11102)
                        continue;
                    AssertEqual(true, condition.Params.Count >= 2, $"{name} Ultima condition parameters");
                    AssertEqual(definition.Id, condition.Params[0], $"{name} Ultima condition owner");
                    ExhibitionRewardTable reward = rewards.Where(row => row.CharacterId == definition.Id
                            && row.LevelId >= condition.Params[1])
                        .OrderBy(row => row.LevelId).First();
                    ultima.Add(definition.Id, (groupId, (uint)defaultSkillId, reward));

                    CharacterData character = new() { Id = (uint)definition.Id, Level = 80, LiberateLv = 0 };
                    AssertEqual(true, Character.MeetsCharacterSkillCondition(character, [conditionId], [reward.Id]),
                        $"{name} claimed exhibition overrides stale low liberation");
                    character.LiberateLv = int.MaxValue;
                    AssertEqual(false, Character.MeetsCharacterSkillCondition(character, [conditionId], []),
                        $"{name} high liberation cannot replace a claim");
                    int unrelatedReward = rewards.First(row => row.CharacterId != definition.Id
                        && row.LevelId >= condition.Params[1]).Id;
                    AssertEqual(false, Character.MeetsCharacterSkillCondition(character, [conditionId], [unrelatedReward]),
                        $"{name} another construct's claim cannot unlock Ultima");
                    int[] lowerClaims = rewards.Where(row => row.CharacterId == definition.Id
                        && row.LevelId < condition.Params[1]).Select(row => row.Id).ToArray();
                    AssertEqual(false, Character.MeetsCharacterSkillCondition(character, [conditionId], lowerClaims),
                        $"{name} below-threshold claims cannot unlock Ultima");
                }
            }
        }
        AssertEqual(definitions.Length, ultima.Count, "Every construct has authoritative Ultima coverage");
        Console.WriteLine($"Ultima table authority: {ultima.Count}/{definitions.Length} constructs have mapped Ultima groups.");

        CharacterTable liv = definitions.Single(row => row.TradeName == "Limpidity");
        CharacterTable nonLiv = definitions.Single(row => row.TradeName == "Crocotta");
        foreach (CharacterTable definition in new[] { liv, nonLiv })
        {
            var target = ultima[definition.Id];
            string name = $"Ultima {definition.Name}: {definition.TradeName}";
            Character roster = CreateTestCharacterRoster(definition.Id, level: 80);
            roster.Uid = 48_700 + Array.IndexOf(definitions, definition);
            CharacterData character = RequiredCharacterData(roster, definition.Id);
            uint[] groupSkills = groups[target.GroupId].SkillId.Where(id => id > 0).Select(id => (uint)id).ToArray();
            character.SkillList.RemoveAll(skill => groupSkills.Contains(skill.Id));
            character.LiberateLv = int.MaxValue;
            Player player = CreateDrawCompatibilityPlayer(roster.Uid);
            player.GatherRewards = [];
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> playerSaves,
                out RecordingMongoCollectionProxy<Character> characterSaves, out _);
            using LoopbackSessionHarness harness = new(roster, player,
                CreateDrawCompatibilityInventory(roster.Uid, []), $"ultima-{definition.TradeName}");
            int packetId = 48_700;

            void Request(int expectedCode, bool expectPush, string scenario)
            {
                int requestId = packetId++;
                InvokeRegisteredRequestHandler(nameof(CharacterUnlockSkillGroupRequest), harness.Session, requestId,
                    new CharacterUnlockSkillGroupRequest { SkillGroupId = target.GroupId });
                if (expectPush)
                {
                    NotifyCharacterDataList push = ReadPushPayload<NotifyCharacterDataList>(harness,
                        nameof(NotifyCharacterDataList), $"{name} {scenario} push first");
                    CharacterData pushed = push.CharacterDataList.Single();
                    AssertEqual((uint)definition.Id, pushed.Id, $"{name} pushed owner");
                    AssertEqual(1, pushed.SkillList.Single(skill => skill.Id == target.SkillId).Level,
                        $"{name} pushed Ultima level");
                }
                CharacterUnlockSkillGroupResponse response = ReadResponsePayload<CharacterUnlockSkillGroupResponse>(harness,
                    requestId, nameof(CharacterUnlockSkillGroupResponse), $"{name} {scenario} response");
                AssertEqual(expectedCode, response.Code, $"{name} {scenario} Code");
                AssertNoAvailablePacket(harness, $"{name} {scenario} exact packet sequence");
            }

            Request(20009021, false, "high liberation without claims");
            player.GatherRewards = [rewards.First(row => row.CharacterId != definition.Id
                && row.LevelId >= target.Reward.LevelId).Id];
            Request(20009021, false, "unrelated claim");
            player.GatherRewards = [target.Reward.Id];
            roster.Characters.Remove(character);
            Request(20009021, false, "unowned character");
            roster.Characters.Add(character);
            AssertEqual(0, characterSaves.ReplaceOneCalls, $"{name} rejections do not save");
            AssertEqual(false, character.SkillList.Any(skill => groupSkills.Contains(skill.Id)),
                $"{name} rejections leave Ultima locked");

            character.LiberateLv = 0;
            Character eligibleReload = BsonSerializer.Deserialize<Character>(roster.ToBson());
            eligibleReload.NormalizeCharactersForCurrentTables(player.GatherRewards);
            AssertEqual(false, RequiredCharacterData(eligibleReload, definition.Id).SkillList
                .Any(skill => groupSkills.Contains(skill.Id)), $"{name} eligibility does not auto-unlock on load");
            player.Save();
            Request(0, true, "retry with valid claim and stale low liberation");
            AssertEqual(1, characterSaves.ReplaceOneCalls, $"{name} successful unlock saved");
            Request(20009047, false, "repeated unlock");
            AssertEqual(1, characterSaves.ReplaceOneCalls, $"{name} repeated unlock does not save");
            AssertEqual(1, character.SkillList.Count(skill => groupSkills.Contains(skill.Id)),
                $"{name} repeated unlock cannot duplicate skill");

            // The recording fixture does not implement AsQueryable/Aggregate used by FromUid.
            // Round-trip saved BSON and invoke the same claimed-reward normalization used on load.
            Player persistedPlayer = BsonSerializer.Deserialize<Player>((playerSaves.LastReplacement
                ?? throw new InvalidDataException($"{name}: missing saved player.")).ToBson());
            byte[] savedCharacter = (characterSaves.LastReplacement
                ?? throw new InvalidDataException($"{name}: missing saved character.")).ToBson();
            Character reloaded = BsonSerializer.Deserialize<Character>(savedCharacter);
            AssertEqual(true, persistedPlayer.GatherRewards.Contains(target.Reward.Id), $"{name} persisted claim");
            AssertEqual(1, RequiredCharacterData(reloaded, definition.Id).SkillList
                .Single(skill => skill.Id == target.SkillId).Level, $"{name} saved skill before load normalization");
            reloaded.NormalizeCharactersForCurrentTables(persistedPlayer.GatherRewards);
            AssertEqual(1, RequiredCharacterData(reloaded, definition.Id).SkillList
                .Single(skill => skill.Id == target.SkillId).Level, $"{name} saved skill survives claimed reload");
            Character unclaimedReload = BsonSerializer.Deserialize<Character>(savedCharacter);
            RequiredCharacterData(unclaimedReload, definition.Id).LiberateLv = int.MaxValue;
            unclaimedReload.NormalizeCharactersForCurrentTables([]);
            AssertEqual(false, RequiredCharacterData(unclaimedReload, definition.Id).SkillList
                .Any(skill => groupSkills.Contains(skill.Id)), $"{name} unclaimed reload cannot retain Ultima");
        }
    }
}
