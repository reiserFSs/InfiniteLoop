using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.guild.boss;
using AscNet.Table.V2.share.robot;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers;

internal static partial class GuildBossModule
{
    internal static bool IsCombatStage(uint stageId) =>
        Rows<GuildBossStageCatalogTable>().Any(row => row.StageId == stageId);

    internal static bool TryPreFight(Session session, PreFightRequest request, out PreFightResponse response)
    {
        response = new();
        if (!IsCombatStage(request.PreFightData.StageId)) return false;
        PreFightResponse prepared = new();
        try
        {
            Guild guild = GuildModule.RequireMembership(session);
            GuildModule.Commit(session, guild, mutation =>
            {
                EnsurePeriod(mutation);
                Require(!IsTemporarilyBanned(DateTimeOffset.UtcNow), 20063364);
                var input = request.PreFightData;
                GuildBossStageState selected = Stage(mutation, checked((int)input.StageId));
                if (selected.Type == 2 && !GuildModule.ConditionSatisfied(session, 7201, []))
                    throw new ServerCodeException("Guild boss high-zone level condition is not satisfied.", 20034003);
                GuildBossParticipantState participant = Participant(mutation);
                if (participant.Stages.Any(row => row.StageId != selected.StageId && row.UploadCount > 0
                    && mutation.Guild.Boss.Stages.Any(other => other.StageId == row.StageId && other.Type == selected.Type)))
                    throw new ServerCodeException("Another guild boss stage of this type was uploaded.", 20063309);
                if ((participant.Stages.SingleOrDefault(row => row.StageId == selected.StageId)?.UploadCount ?? 0)
                    >= ConfigInt("GuildBossStageUploadCount"))
                    throw new ServerCodeException("Guild boss upload limit reached.", 20063305);
                GuildBossPlayerState player = mutation.Player(session.player.PlayerData.Id).Boss;
                if (input.ChallengeCount is < 0 or > 1 || input.IsHasAssist || input.SpeedrunStageId != 0 || input.SelectAreaId != 0)
                    throw new ServerCodeException("Invalid guild boss battle request.", 20063300);
                input.ChallengeCount = 1;
                ValidateBossTeam(session, mutation.Guild, selected, input);
                GuildBossAttemptState? previous = player.Attempt;
                if (previous is { Settled: false } && previous.GuildId == mutation.Guild.Id
                    && previous.Period == mutation.Guild.Boss.Period && previous.StageId == input.StageId
                    && previous.CaptainPos == input.CaptainPos && previous.FirstFightPos == input.FirstFightPos
                    && previous.CardIds.SequenceEqual(input.CardIds!.Select(id => checked((int)id)))
                    && previous.RobotIds.SequenceEqual(input.RobotIds!) && previous.PreFightBytes.Length > 0)
                {
                    prepared = MessagePackSerializer.Deserialize<PreFightResponse>(previous.PreFightBytes);
                    return;
                }
                GuildBossAttemptState attempt = new()
                {
                    GuildId = mutation.Guild.Id, Period = mutation.Guild.Boss.Period, StageId = input.StageId,
                    FightId = Random.Shared.NextInt64(1, int.MaxValue), Seed = Random.Shared.NextInt64(0, (long)uint.MaxValue + 1),
                    StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), CaptainPos = input.CaptainPos,
                    FirstFightPos = input.FirstFightPos, CardIds = input.CardIds!.Select(id => checked((int)id)).ToList(),
                    RobotIds = input.RobotIds!.ToList()
                };
                prepared.FightData = BuildBossFight(session, player, attempt, input);
                attempt.PreFightBytes = MessagePackSerializer.Serialize(prepared);
                player.Attempt = attempt;
            });
            session.fight = new(request, prepared.FightData.FightId);
        }
        catch (ServerCodeException exception) { prepared = new() { Code = exception.Code }; }
        catch (OverflowException) { prepared = new() { Code = 20063300 }; }
        response = prepared;
        return true;
    }

    private static void ValidateBossTeam(Session session, Guild guild, GuildBossStageState stage,
        PreFightRequest.PreFightRequestPreFightData input)
    {
        if (input.CardIds is null || input.RobotIds is null || input.CardIds.Count is < 1 or > 3
            || input.CardIds.Count != input.RobotIds.Count || input.CaptainPos < 1 || input.FirstFightPos < 1
            || input.CaptainPos > input.CardIds.Count || input.FirstFightPos > input.CardIds.Count)
            throw new ServerCodeException("Invalid guild boss team.", 20004003);
        HashSet<int> characters = [];
        for (int slot = 0; slot < input.CardIds.Count; slot++)
        {
            int card = checked((int)input.CardIds[slot]);
            int robot = input.RobotIds[slot];
            if (robot < 0 || (card != 0 && robot != 0))
                throw new ServerCodeException("Invalid guild boss slot.", 20004003);
            if (card == 0 && robot == 0) continue;
            int character = card;
            if (robot != 0)
            {
                if (!guild.Boss.Robots.Any(row => row.Type == stage.Type && row.RobotIds.Contains(robot)))
                    throw new ServerCodeException("Guild boss robot is not available this week.", 20063299);
                character = Rows<RobotTable>().SingleOrDefault(row => row.Id == robot)?.CharacterId
                    ?? throw new ServerCodeException("Guild boss robot does not exist.", 20063298);
            }
            else if (!session.character.Characters.Any(row => row.Id == card))
                throw new ServerCodeException("Guild boss character is not owned.", 20004003);
            if (!characters.Add(character)) throw new ServerCodeException("Duplicate guild boss character.", 20004003);
        }
        if (characters.Count == 0
            || (input.CardIds[input.CaptainPos - 1] == 0 && input.RobotIds[input.CaptainPos - 1] == 0)
            || (input.CardIds[input.FirstFightPos - 1] == 0 && input.RobotIds[input.FirstFightPos - 1] == 0))
            throw new ServerCodeException("Guild boss captain or first fighter is absent.", 20004003);
    }

    private static PreFightResponse.PreFightResponseFightData BuildBossFight(Session session, GuildBossPlayerState player,
        GuildBossAttemptState attempt, PreFightRequest.PreFightRequestPreFightData input)
    {
        StageTable stage = Rows<StageTable>().SingleOrDefault(row => row.StageId == attempt.StageId)
            ?? throw new ServerCodeException("Guild boss stage is missing.", 20063297);
        StageLevelControlTable? levelControl = Rows<StageLevelControlTable>()
            .Where(row => row.StageId == attempt.StageId)
            .MinBy(row => Math.Abs((long)session.player.PlayerData.Level - row.MaxLevel));
        PreFightResponse.PreFightResponseFightData data = new()
        {
            FightId = checked((uint)attempt.FightId), Seed = checked((uint)attempt.Seed), StageId = checked((uint)attempt.StageId),
            RebootId = stage.RebootId ?? 0, PassTimeLimit = stage.PassTimeLimit ?? 0,
            MonsterLevel = levelControl?.MonsterLevel ?? [],
            NormalEventIds = stage.NormalEventId.Select(id => (dynamic)id).ToList(),
            Restartable = Convert.ToInt32(stage.Restartable) != 0
        };
        foreach (int skillId in player.FightStyle.EffectedSkillId.Concat(Rows<GuildBossFightStyleSkillTable>()
            .Where(row => row.Style == player.FightStyle.StyleId && row.IsPermanent is > 0).Select(row => row.Id)).Distinct())
        {
            var skill = Rows<GuildBossFightStyleSkillTable>().SingleOrDefault(row => row.Id == skillId && row.Style == player.FightStyle.StyleId)
                ?? throw new ServerCodeException("Guild boss fight style is invalid.", 20063300);
            data.EventIds.AddRange(skill.FightEventId.Select(id => (dynamic)id));
        }
        PreFightResponse.PreFightResponseFightData.PreFightResponseFightDataRoleData role = new()
        {
            Id = checked((uint)session.player.PlayerData.Id), Camp = 1, Name = session.player.PlayerData.Name,
            CaptainIndex = attempt.CaptainPos - 1, FirstFightPos = attempt.FirstFightPos - 1,
            EnterCgIndex = input.EnterCgIndex - 1, SettleCgIndex = input.SettleCgIndex - 1, NpcData = []
        };
        for (int slot = 0; slot < attempt.CardIds.Count; slot++)
        {
            int card = attempt.CardIds[slot], robotId = attempt.RobotIds[slot];
            if (card == 0 && robotId == 0)
            {
                attempt.CharacterHeadInfoList.Add(new());
                continue;
            }
            CharacterData character;
            IReadOnlyList<EquipData> equips;
            int weaponFashion;
            if (robotId != 0)
            {
                RobotTable robot = Rows<RobotTable>().Single(row => row.Id == robotId);
                var deployment = FightModule.BuildRobotDeployment(robot);
                character = deployment.Character;
                equips = deployment.Equips;
                weaponFashion = robot.WeaponFashion ?? 0;
            }
            else
            {
                character = session.character.Characters.Single(row => row.Id == card);
                equips = FightModule.BuildTeamPrefabFightEquips(session, checked((uint)card));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                weaponFashion = session.character.WeaponFashions.FirstOrDefault(row =>
                    (row.ExpireTime == 0 || row.ExpireTime > now) && row.UseCharacterList.Contains(card))?.Id ?? 0;
            }
            attempt.CharacterHeadInfoList.Add(new()
            {
                HeadFashionId = character.CharacterHeadInfo?.HeadFashionId ?? character.FashionId,
                HeadFashionType = character.CharacterHeadInfo?.HeadFashionType ?? 0
            });
            role.NpcData[slot] = new
            {
                Character = character, Equips = equips, WeaponFashionId = weaponFashion, EventIds = Array.Empty<int>(),
                Partner = robotId == 0 ? session.character.Partners.FirstOrDefault(row => row.CharacterId == character.Id) : null,
                IsRobot = robotId != 0, RobotId = robotId, IsNpc = false,
                CharacterCareer = Rows<CharacterTable>().Single(row => row.Id == character.Id).Career,
                MagicIds = new Dictionary<int, int>()
            };
        }
        var deployed = role.NpcData.Values.Select(npc => (CharacterData)npc.Character).ToArray();
        foreach (dynamic npc in role.NpcData.Values)
            foreach (var magic in FightModule.BuildObservationMagicIds(deployed, (CharacterData)npc.Character))
                npc.MagicIds.Add(magic.Key, magic.Value);
        data.RoleData.Add(role);
        return data;
    }

    internal static bool TrySettleFight(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        response = new();
        GuildBossAttemptState? pending = session.player.GuildState.Boss.Attempt;
        if (!IsCombatStage(result.StageId) && (pending is null || pending.FightId != result.FightId)) return false;
        FightSettleResponse settled = new();
        try
        {
            Guild guild = GuildModule.RequireMembership(session);
            GuildModule.Commit(session, guild, mutation =>
            {
                EnsurePeriod(mutation);
                Require(!IsTemporarilyBanned(DateTimeOffset.UtcNow), 20063364);
                GuildBossAttemptState attempt = mutation.Player(session.player.PlayerData.Id).Boss.Attempt
                    ?? throw new ServerCodeException("Guild boss attempt is missing.", 20063300);
                Stage(mutation, checked((int)result.StageId));
                if (attempt.GuildId != mutation.Guild.Id || attempt.Period != mutation.Guild.Boss.Period
                    || attempt.StageId != result.StageId || attempt.FightId != result.FightId || attempt.FightId == 0)
                    throw new ServerCodeException("Guild boss settlement does not match the attempt.", 20063300);
                byte[] requestBytes = MessagePackSerializer.Serialize(result);
                if (attempt.Settled)
                {
                    if (!attempt.SettleRequest.AsSpan().SequenceEqual(requestBytes))
                        throw new ServerCodeException("Guild boss settlement changed after acceptance.", 20063304);
                    settled = MessagePackSerializer.Deserialize<FightSettleResponse>(attempt.SettleData);
                    return;
                }
                if (attempt.Uploaded) throw new ServerCodeException("Guild boss attempt has already been uploaded.", 20063304);
                ValidateBossResult(session, attempt, result);
                long frames = checked(result.SettleFrame - result.StartFrame - result.PauseFrame - result.ExSkillPauseFrame);
                if (frames < 0 || frames / 20 > int.MaxValue) throw new ServerCodeException("Invalid guild boss duration.", 20063304);
                double ratio = ConfigDouble("GuildBossScoreCollectionRatio");
                if (!double.IsFinite(ratio) || ratio <= 0) throw new ServerCodeException("Guild boss score rule is invalid.", 20063301);
                // Explicit local policy: cap raw damage by the period's initial shared HP in score units.
                // DPS consistency is not authoritative combat simulation or anti-cheat.
                decimal maximumDamage = decimal.Floor(mutation.Guild.Boss.HpMax / (decimal)ratio);
                if (result.TotalDamage > maximumDamage)
                    throw new ServerCodeException("Guild boss damage exceeds the local period bound.", 20063304);
                long score = checked((long)decimal.Floor(checked(result.TotalDamage * (decimal)ratio)));
                GuildBossParticipantState participant = Participant(mutation);
                long best = participant.Stages.SingleOrDefault(row => row.StageId == result.StageId)?.Score ?? 0;
                _ = checked(mutation.Guild.Boss.GuildScoreSumBest + Math.Max(0, score - best));
                bool won = result.IsWin && !result.IsForceExit;
                attempt.Result = new()
                {
                    Damage = result.TotalDamage, DamageScore = score, TotalScore = score, TotalHighScore = best,
                    UseTime = checked((int)(frames / 20)), HpLeftPer = BossTeamHp(attempt, result)
                };
                attempt.Won = won;
                attempt.Settled = true;
                settled.Settle = new()
                {
                    IsWin = won, StageId = result.StageId, LeftTime = checked((int)result.LeftTime),
                    NpcHpInfo = result.NpcHpInfo, ChallengeCount = 1, GuildBossFightResult = attempt.Result
                };
                attempt.SettleRequest = requestBytes;
                attempt.SettleData = MessagePackSerializer.Serialize(settled);
            });
            session.fight = null;
        }
        catch (ServerCodeException exception)
        {
            Server.log.Warn($"Guild siege settlement rejected: code={exception.Code}, reason={exception.Message}, "
                + $"frames={result.StartFrame}/{result.SettleFrame}/{result.PauseFrame}/{result.ExSkillPauseFrame}, "
                + $"leftTime={result.LeftTime}, rebootCount={result.RebootCount}, "
                + $"damage={result.TotalDamage}, highestDamage={result.HighestDamage}, "
                + $"damaged={result.TotalDamaged}, cure={result.TotalCure}, "
                + $"playerCount={result.PlayerIds?.Length ?? 0}, dpsCount={result.NpcDpsTable?.Count ?? 0}, "
                + $"isWin={result.IsWin}, forceExit={result.IsForceExit}.");
            settled = new() { Code = exception.Code };
        }
        catch (OverflowException)
        {
            Server.log.Warn("Guild siege settlement rejected: code=20063304, reason=numeric overflow.");
            settled = new() { Code = 20063304 };
        }
        response = settled;
        return true;
    }

    private static HashSet<int> BossCharacters(GuildBossAttemptState attempt) => attempt.CardIds
        .Select((card, slot) => card != 0 ? card : attempt.RobotIds[slot] == 0 ? 0
            : Rows<RobotTable>().Single(row => row.Id == attempt.RobotIds[slot]).CharacterId)
        .Where(id => id != 0).ToHashSet();

    private static void ValidateBossResult(Session session, GuildBossAttemptState attempt, FightSettleResult result)
    {
        if (result.StartFrame < 0 || result.SettleFrame < result.StartFrame || result.PauseFrame < 0 || result.ExSkillPauseFrame < 0
            || result.TotalDamage < 0 || result.HighestDamage < 0 || result.HighestDamage > result.TotalDamage
            || result.TotalDamaged < 0 || result.TotalCure < 0 || result.LeftTime < 0 || result.LeftTime > int.MaxValue
            || result.RebootCount != 0 || (result.PlayerIds is { Length: > 0 }
                && (result.PlayerIds.Length != 1 || result.PlayerIds[0] != session.player.PlayerData.Id)))
            throw new ServerCodeException("Invalid guild boss settlement bounds.", 20063304);
        HashSet<int> characters = BossCharacters(attempt);
        if (result.NpcHpInfo is null || !characters.SetEquals(result.NpcHpInfo.Values.Where(row => row.Type == 1).Select(row => row.CharacterId))
            || result.NpcHpInfo.Values.Count(row => row.Type == 1) != characters.Count)
            throw new ServerCodeException("Guild boss settlement team changed.", 20063300);
        if (result.NpcDpsTable is null)
            throw new ServerCodeException("Guild boss damage records are missing.", 20063304);
        long damage = 0;
        HashSet<int> damageCharacters = [];
        foreach (NpcDpsTable npc in result.NpcDpsTable.Values)
        {
            if (npc.DamageTotal < 0 || npc.DamageNormal < 0 || npc.DamageMagic is null || npc.DamageMagic.Any(value => value < 0))
                throw new ServerCodeException("Invalid guild boss damage record.", 20063304);
            // Native DPS records have no Type field; their owner and deployed character identify the team.
            if (npc.RoleId == 0 && npc.CharacterId == 0) continue;
            if (!characters.Contains(npc.CharacterId) || npc.RoleId != session.player.PlayerData.Id || !damageCharacters.Add(npc.CharacterId))
                throw new ServerCodeException("Guild boss damage team changed.", 20063300);
            damage = checked(damage + npc.DamageTotal);
        }
        if (damage != result.TotalDamage)
            throw new ServerCodeException($"Guild boss total damage does not match deployed character damage: reported={result.TotalDamage}, recorded={damage}.", 20063304);
    }

    private static double BossTeamHp(GuildBossAttemptState attempt, FightSettleResult result)
    {
        double total = 0;
        foreach (NpcHp npc in result.NpcHpInfo.Values.Where(row => row.Type == 1))
        {
            if (npc.AttrTable is null || !npc.AttrTable.TryGetValue(1, out dynamic? attribute))
                throw new ServerCodeException("Guild boss character HP is missing.", 20063304);
            object value = attribute;
            var map = value as System.Collections.IDictionary;
            object? current = map?["Value"], maximum = map?["MaxValue"];
            if (value is JObject json) { current = json["Value"]; maximum = json["MaxValue"]; }
            if (current is null || maximum is null)
                throw new ServerCodeException("Guild boss character HP is missing.", 20063304);
            double hp, max;
            try { hp = Convert.ToDouble(current, CultureInfo.InvariantCulture); max = Convert.ToDouble(maximum, CultureInfo.InvariantCulture); }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            { throw new ServerCodeException("Guild boss character HP is invalid.", 20063304); }
            if (!double.IsFinite(hp) || !double.IsFinite(max) || max <= 0 || hp < 0 || hp > max)
                throw new ServerCodeException("Guild boss character HP is invalid.", 20063304);
            total += hp / max * 100;
        }
        return total / BossCharacters(attempt).Count;
    }

    internal static void OnMembershipChanged(long uid)
    {
        Session? session = Server.Instance.SessionFromUID(uid);
        GuildBossAttemptState? attempt = session?.player.GuildState.Boss.Attempt;
        if (session is null || attempt is null) return;
        Guild? guild = GuildModule.FindMembership(uid);
        if (guild is not null && guild.Id == attempt.GuildId && GuildModule.Rank(guild, uid) is >= 1 and <= 4) return;
        if (session.fight?.FightId == attempt.FightId) session.fight = null;
    }
}
