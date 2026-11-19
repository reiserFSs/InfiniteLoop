using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.dormitory.furniture;
using AscNet.Table.V2.share.headportrait;
using AscNet.Table.V2.share.medal;
using AscNet.Table.V2.share.nameplate;
using AscNet.Table.V2.share.scoretitle;

namespace AscNet.GameServer.Commands
{
    [CommandName("unlock")]
    internal sealed class UnlockCommand : Command
    {
        // Condition.Type the client uses for score-title quality thresholds (Params[0] = title id, Params[1] = score).
        private const int ScoreTitleConditionType = 15103;
        private const string FurnitureCapKey = "DormMaxTotalFurnitureCount";

        public UnlockCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help =>
            "Unlock cosmetics the 4.9 client has. Forms: /unlock <medal|nameplate|emoji|title|chatboard|portrait> <id|all> | " +
            "/unlock furniture <furnitureId> [count]. 'portrait' covers portraits and frames. " +
            "'all' unlocks every obtainable entry (nameplates: the top tier of each group); timed entries keep their table duration. " +
            "Furniture is added to the dorm storage up to the dorm furniture cap. Replies with the counts.";

        [Argument(0, @"^(medal|nameplate|emoji|title|chatboard|portrait|furniture)$", "Kind: medal, nameplate, emoji, title, chatboard, portrait or furniture", ArgumentFlags.IgnoreCase)]
        string Kind { get; set; } = string.Empty;

        [Argument(1, @"^([0-9]+|all)$", "Table id, or 'all' (furniture: the furniture id)", ArgumentFlags.IgnoreCase)]
        string Target { get; set; } = string.Empty;

        [Argument(2, @"^[0-9]+$", "Furniture only: how many to add (default 1)", ArgumentFlags.Optional)]
        string Count { get; set; } = string.Empty;

        private static CommandMessageCallbackException Fail(string message) => new(message);

        public override void Execute()
        {
            string kind = Kind.ToLowerInvariant();
            bool all = Target.Equals("all", StringComparison.OrdinalIgnoreCase);
            int id = 0;
            if (!all && !int.TryParse(Target, out id))
                throw Fail($"Invalid id {Target}.");
            if (kind != "furniture" && Count != "")
                throw Fail("A count only applies to furniture.");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            switch (kind)
            {
                case "medal": Medals(all, id, now); break;
                case "nameplate": Grant("nameplates", Nameplates(all, id, now)); break;
                case "emoji": Grant("emojis", Emojis(all, id, now)); break;
                case "title": Grant("titles", Titles(all, id)); break;
                case "chatboard": Grant("chat boards", ChatBoards(all, id, now)); break;
                case "portrait": Grant("portraits", Portraits(all, id)); break;
                default:
                    if (all)
                        throw Fail("Furniture takes a furniture id, not 'all'.");
                    Furniture(id);
                    break;
            }
        }

        // What a kind resolves to: rewards to apply, plus what was left alone.
        private sealed record Plan(List<Reward> Rewards, int Total, int Already, int Unsupported, int Timed);

        private static List<T> Pick<T>(IEnumerable<T> rows, Func<T, int> idOf, bool all, int id, string what)
        {
            List<T> picked = all ? rows.ToList() : rows.Where(row => idOf(row) == id).ToList();
            if (picked.Count == 0)
                throw Fail(all ? $"The client has no {what}." : $"Unknown {what} id {id}.");
            return picked;
        }

        private void Medals(bool all, int id, long now)
        {
            List<MedalTable> rows = Pick(TableReaderV2.Parse<MedalTable>(), row => row.Id, all, id, "medal");
            List<MedalUnlockState> changed = [];
            int already = 0, timed = 0;
            foreach (MedalTable row in rows)
            {
                long keepTime = Convert.ToInt64(row.KeepTime);
                MedalUnlockState? owned = session.player.UnlockedMedals.Find(medal => medal.Id == row.Id);
                if (owned is not null && (owned.KeepTime == 0 || owned.Time + owned.KeepTime > now))
                {
                    already++;
                    continue;
                }
                if (owned is null)
                    session.player.UnlockedMedals.Add(owned = new MedalUnlockState { Id = row.Id });
                owned.Time = now;
                owned.KeepTime = keepTime;
                changed.Add(owned);
                if (keepTime > 0)
                    timed++;
            }

            if (changed.Count > 0)
            {
                session.player.SaveChecked();
                session.SendPush(new NotifyMedalData
                {
                    MedalInfos = changed.Select(medal => new NotifyMedalData.NotifyMedalDataMedalInfo
                    {
                        Id = medal.Id,
                        Time = medal.Time,
                        KeepTime = medal.KeepTime
                    }).ToList()
                });
            }
            throw Fail(Report("medals", changed.Count, rows.Count, already, 0, timed));
        }

        private Plan Nameplates(bool all, int id, long now)
        {
            List<NameplateTable> rows = Pick(TableReaderV2.Parse<NameplateTable>(), row => row.Id, all, id, "nameplate");
            int unsupported = rows.Count(row => !Character.IsGrantableNameplate(row));
            if (!all && unsupported > 0)
                throw Fail($"Nameplate {id} is not a directly awardable tier (it is converted or promoted by the game).");

            // A group holds one nameplate at its best owned tier, so 'all' asks for each group's top tier.
            IEnumerable<NameplateTable> targets = rows.Where(row => Character.IsGrantableNameplate(row));
            if (all)
                targets = targets.GroupBy(row => row.Group).Select(group => group.MaxBy(row => row.NameplateQuality)!);

            List<Reward> rewards = [];
            int already = 0, timed = 0;
            foreach (NameplateTable target in targets)
            {
                NameplateData? owned = session.character.Nameplates
                    .FirstOrDefault(value => Character.GetNameplateConfig(value.Id)?.Group == target.Group);
                if (owned is not null && (owned.EndTime == 0 || owned.EndTime > now)
                    && Character.GetNameplateConfig(owned.Id)!.NameplateQuality >= target.NameplateQuality)
                {
                    already++;
                    continue;
                }
                rewards.Add(new Reward { Id = target.Id, Type = RewardType.Nameplate });
                if (target.Duration > 0)
                    timed++;
            }
            return new Plan(rewards, all ? rows.Count : 1, already, all ? unsupported : 0, timed);
        }

        private Plan Emojis(bool all, int id, long now)
        {
            List<EmojiTable> rows = Pick(TableReaderV2.Parse<EmojiTable>(), row => row.Id, all, id, "emoji");
            List<Reward> rewards = [];
            int already = 0, timed = 0;
            foreach (EmojiTable row in rows)
            {
                // Free and permanent emojis are usable by everyone; only timed ones need an owned entry.
                if (session.character.CanUseChatEmoji(row.Id, now))
                {
                    already++;
                    continue;
                }
                rewards.Add(new Reward { Id = row.Id, Type = RewardType.ChatEmoji });
                timed++;
            }
            return new Plan(rewards, rows.Count, already, 0, timed);
        }

        private Plan Titles(bool all, int id)
        {
            List<ScoreTitleTable> rows = Pick(TableReaderV2.Parse<ScoreTitleTable>(), row => row.Id, all, id, "title");
            Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
                .Where(row => row.Type == ScoreTitleConditionType)
                .ToDictionary(row => row.Id);

            List<Reward> rewards = [];
            int already = 0, unsupported = 0;
            foreach (ScoreTitleTable row in rows)
            {
                // Same rule the reward path enforces: every quality step needs a score condition for this title.
                if (row.QualityConditions.Count != row.Qualities.Count
                    || row.QualityConditions.Any(conditionId => !conditions.TryGetValue(conditionId, out ConditionTable? condition)
                        || condition.Params.Count < 2 || condition.Params[0] != row.Id))
                {
                    if (!all)
                        throw Fail($"Title {id} has quality conditions the server cannot award.");
                    unsupported++;
                    continue;
                }

                // The reward adds score, so ask for exactly what reaches the highest quality step.
                int topScore = row.QualityConditions.Select(conditionId => conditions[conditionId].Params[1]).DefaultIfEmpty(1).Max();
                int owned = session.character.ScoreTitles?.FirstOrDefault(title => title.Id == (uint)row.Id)?.Score ?? 0;
                if (owned >= topScore)
                {
                    already++;
                    continue;
                }
                rewards.Add(new Reward { Id = row.Id, Type = RewardType.Collection, Count = topScore - owned });
            }
            return new Plan(rewards, rows.Count, already, unsupported, 0);
        }

        private Plan ChatBoards(bool all, int id, long now)
        {
            List<ChatBoardTable> rows = Pick(TableReaderV2.Parse<ChatBoardTable>(), row => row.Id, all, id, "chat board");
            List<Reward> rewards = [];
            int already = 0, timed = 0;
            foreach (ChatBoardTable row in rows)
            {
                ChatBoardUnlockState? owned = session.player.UnlockedChatBoards?.Find(unlock => unlock.Id == row.Id);
                if (owned is not null && (owned.EndTime == 0 || owned.EndTime > now))
                {
                    already++;
                    continue;
                }
                rewards.Add(new Reward { Id = row.Id, Type = RewardType.ChatBoard });
                if (row.Duration > 0)
                    timed++;
            }
            return new Plan(rewards, rows.Count, already, 0, timed);
        }

        private Plan Portraits(bool all, int id)
        {
            List<HeadPortraitTable> rows = Pick(TableReaderV2.Parse<HeadPortraitTable>(), row => row.Id, all, id, "portrait");
            List<Reward> rewards = [];
            int already = 0, unsupported = 0, timed = 0;
            foreach (HeadPortraitTable row in rows)
            {
                // Expire marks a retired frame the client hides unless it is still held.
                if (Convert.ToInt32(row.Expire) != 0)
                {
                    if (!all)
                        throw Fail($"Portrait {id} is retired and hidden by the client.");
                    unsupported++;
                    continue;
                }
                if (session.player.HeadPortraits.Any(head => head.Id == row.Id))
                {
                    already++;
                    continue;
                }
                rewards.Add(new Reward { Id = row.Id, Type = RewardType.HeadPortrait });
                if (Convert.ToInt32(row.LimitType) != 0)
                    timed++;
            }
            return new Plan(rewards, rows.Count, already, unsupported, timed);
        }

        // Applies each reward through the normal grant path, persists and sends the usual pushes.
        private void Grant(string noun, Plan plan)
        {
            RewardApplicationResult pushes = new();
            int granted = 0, rejected = 0;
            foreach (Reward reward in plan.Rewards)
            {
                try
                {
                    pushes.AddPushes(RewardHandler.ApplyRewards([reward], session));
                    granted++;
                }
                catch (InvalidDataException)
                {
                    rejected++;
                }
            }

            if (granted > 0)
            {
                session.character.SaveChecked();
                session.player.SaveChecked();
                if (pushes.ItemData.ItemDataList.Count > 0)
                    session.inventory.SaveChecked();
                pushes.SendPushes(session);
            }
            throw Fail(Report(noun, granted, plan.Total, plan.Already, plan.Unsupported + rejected, plan.Timed));
        }

        private void Furniture(int furnitureId)
        {
            if (!TableReaderV2.Parse<FurnitureTable>().Any(row => row.Id == furnitureId))
                throw Fail($"Unknown furniture id {furnitureId}.");
            int count = 1;
            if (Count != "" && (!int.TryParse(Count, out count) || count <= 0))
                throw Fail("The furniture count must be a positive number.");

            // Grants go through a FurnitureReward row (it fixes the attributes); rows without one are not obtainable.
            FurnitureRewardTable? reward = TableReaderV2.Parse<FurnitureRewardTable>()
                .FirstOrDefault(row => row.FurnitureId == furnitureId);
            if (reward is null)
                throw Fail($"Furniture {furnitureId} has no reward entry and cannot be granted.");

            int cap = int.TryParse(TableReaderV2.Parse<ConfigTable>().FirstOrDefault(row => row.Key == FurnitureCapKey)?.Value, out int configured)
                ? configured : int.MaxValue;
            int stored = session.player.Dorm.Furniture.Count;
            if (count > cap - stored)
                throw Fail($"Dorm storage holds {stored} of {cap} furniture; only {Math.Max(0, cap - stored)} more fit.");

            RewardApplicationResult result;
            try
            {
                result = RewardHandler.ApplyRewards([new Reward { Id = reward.Id, Type = RewardType.Furniture, Count = count }], session);
            }
            catch (InvalidDataException)
            {
                throw Fail($"Furniture {furnitureId} was rejected by the dorm rules.");
            }

            session.player.SaveChecked();
            result.SendPushes(session);
            // The client only learns furniture from the dorm data, so resend it (same payload as login).
            session.SendPush(DormModule.BuildLoginData(session));
            throw Fail($"Added {count} x furniture {furnitureId}; dorm storage now {stored + count} of {cap}.");
        }

        private static string Report(string noun, int granted, int total, int already, int skipped, int timed)
        {
            string text = $"Unlocked {granted} {noun}; {already} already owned; {total} in the client table";
            if (skipped > 0)
                text += $"; {skipped} skipped (the server cannot award them)";
            if (timed > 0 && granted > 0)
                text += $"; timed entries keep their table duration";
            return text + ".";
        }
    }
}
