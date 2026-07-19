using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.client.lifetree;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.mainline2;
using AscNet.Table.V2.share.lifetree;
using MessagePack;
using MessagePack.Formatters;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackFormatter(typeof(LifeTreeFinishProcessRequestFormatter))]
    [MessagePackObject(true)]
    public class LifeTreeFinishProcessRequest
    {
        public int? ProcessId { get; set; }
        public int Process { get; set; }
    }

    internal sealed class LifeTreeFinishProcessRequestFormatter : IMessagePackFormatter<LifeTreeFinishProcessRequest?>
    {
        public void Serialize(ref MessagePackWriter writer, LifeTreeFinishProcessRequest? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }
            if (value.ProcessId.HasValue)
            {
                writer.WriteMapHeader(2);
                writer.Write(nameof(LifeTreeFinishProcessRequest.ProcessId));
                writer.Write(value.ProcessId.Value);
            }
            else
            {
                writer.WriteMapHeader(1);
            }
            writer.Write(nameof(LifeTreeFinishProcessRequest.Process));
            writer.Write(value.Process);
        }

        public LifeTreeFinishProcessRequest? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
                return null;
            int count = reader.ReadMapHeader();
            LifeTreeFinishProcessRequest value = new();
            for (int index = 0; index < count; index++)
            {
                switch (reader.ReadString())
                {
                    case nameof(LifeTreeFinishProcessRequest.Process):
                        value.Process = reader.ReadInt32();
                        break;
                    case nameof(LifeTreeFinishProcessRequest.ProcessId):
                        value.ProcessId = reader.TryReadNil() ? null : reader.ReadInt32();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return value;
        }
    }

    [MessagePackObject(true)]
    public class LifeTreeFinishProcessResponse
    {
        public int Code { get; set; }
        public bool IsFinishGuide { get; set; }
        public bool IsFinishLifeTreePv { get; set; }
        public List<int> FinishedChapters { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class LifeTreeUnlockCharacterRequest
    {
        public int CharacterId { get; set; }
        public int Status { get; set; }
    }

    [MessagePackObject(true)]
    public class LifeTreeUnlockCharacterResponse
    {
        public int Code { get; set; }
        public LifeTreeUnlockCharacterData CharacterData { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class LifeTreeModule
    {
        private const int InvalidRequest = 20326007;
        private const int StateError = 20326008;
        private static readonly Lazy<Dictionary<int, LifeTreeChapterTable>> Chapters = new(() =>
            TableReaderV2.Parse<LifeTreeChapterTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<HashSet<int>> ExhibitionChapters = new(() =>
            TableReaderV2.Parse<MainLine2ExhibitionChapterTable>().Select(row => row.Id).ToHashSet());
        private static readonly Lazy<int?> CurrentPopupChapter = new(() =>
        {
            LifeTreeClientConfigTable? config = TableReaderV2.Parse<LifeTreeClientConfigTable>()
                .SingleOrDefault(row => row.Key == "CurVersionChapterId");
            return int.TryParse(config?.Values.FirstOrDefault(), out int chapterId)
                && ExhibitionChapters.Value.Contains(chapterId)
                ? chapterId
                : null;
        });
        private static readonly Lazy<HashSet<int>> NavigableChapters = new(() =>
        {
            HashSet<int> chapterIds = Chapters.Value.Keys
                .Where(ExhibitionChapters.Value.Contains)
                .ToHashSet();
            if (CurrentPopupChapter.Value is int chapterId)
                chapterIds.Add(chapterId);
            return chapterIds;
        });
        private static readonly Lazy<Dictionary<int, LifeTreeCharacterTable>> Characters = new(() =>
            TableReaderV2.Parse<LifeTreeCharacterTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() =>
            TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));

        [RequestPacketHandler("LifeTreeFinishProcessRequest")]
        public static void LifeTreeFinishProcessRequestHandler(Session session, Packet.Request packet)
        {
            LifeTreeFinishProcessRequest? request = packet.Deserialize<LifeTreeFinishProcessRequest>();
            NotifyLifeTreeData? original = session.player.LifeTreeData;
            NotifyLifeTreeData responseData = Clone(original ?? new());
            int code = InvalidRequest;
            if (request is not null)
            {
                NotifyLifeTreeData staged = Clone(responseData);
                code = FinishProcess(staged, request);
                if (code == 0)
                {
                    session.player.LifeTreeData = staged;
                    try
                    {
                        session.player.SaveChecked();
                        responseData = staged;
                    }
                    catch (Exception exception)
                    {
                        session.player.LifeTreeData = original!;
                        code = StateError;
                        session.log.Error($"Failed to persist LifeTree process: {exception}");
                    }
                }
            }

            session.SendResponse(new LifeTreeFinishProcessResponse
            {
                Code = code,
                IsFinishGuide = responseData.IsFinishGuide,
                IsFinishLifeTreePv = responseData.IsFinishLifeTreePv,
                FinishedChapters = responseData.FinishedChapters.ToList()
            }, packet.Id);
        }

        [RequestPacketHandler("LifeTreeUnlockCharacterRequest")]
        public static void LifeTreeUnlockCharacterRequestHandler(Session session, Packet.Request packet)
        {
            LifeTreeUnlockCharacterRequest? request = packet.Deserialize<LifeTreeUnlockCharacterRequest>();
            if (request is null)
            {
                session.SendResponse(new LifeTreeUnlockCharacterResponse { Code = InvalidRequest }, packet.Id);
                return;
            }

            NotifyLifeTreeData? originalLifeTree = session.player.LifeTreeData;
            MissionProgressState originalMission = session.player.MissionProgress;
            session.player.LifeTreeData = Clone(originalLifeTree ?? new());
            session.player.MissionProgress = Clone(originalMission);

            int code = UnlockCharacter(session, request, out LifeTreeUnlockCharacterData characterData);
            NotifyTask? taskProgress = null;
            if (code == 0)
            {
                taskProgress = TaskModule.ApplyLifeTreeUnlockProgress(
                    session.player, request.CharacterId, request.Status);
                try
                {
                    session.player.SaveChecked();
                }
                catch (Exception exception)
                {
                    session.player.LifeTreeData = originalLifeTree!;
                    session.player.MissionProgress = originalMission;
                    taskProgress = null;
                    code = StateError;
                    session.log.Error($"Failed to persist LifeTree character unlock: {exception}");
                }
            }
            else
            {
                session.player.LifeTreeData = originalLifeTree!;
                session.player.MissionProgress = originalMission;
            }

            if (code == 0 && taskProgress is not null)
                session.SendPush(taskProgress);
            session.SendResponse(new LifeTreeUnlockCharacterResponse
            {
                Code = code,
                CharacterData = characterData
            }, packet.Id);
        }

        private static NotifyLifeTreeData Clone(NotifyLifeTreeData source) => new()
        {
            IsFinishGuide = source.IsFinishGuide,
            IsFinishLifeTreePv = source.IsFinishLifeTreePv,
            FinishedChapters = source.FinishedChapters.ToList(),
            UnlockCharacterData = source.UnlockCharacterData.ToDictionary(
                pair => pair.Key,
                pair => new LifeTreeUnlockCharacterData
                {
                    Id = pair.Value.Id,
                    UnlockStatus = pair.Value.UnlockStatus
                })
        };

        private static MissionProgressState Clone(MissionProgressState source) => new()
        {
            ConditionCounters = new(source.ConditionCounters),
            ClaimedTaskIds = source.ClaimedTaskIds.ToList(),
            NewPlayerRewardRecords = source.NewPlayerRewardRecords.ToList(),
            NewbieRewardRecords = source.NewbieRewardRecords.ToList(),
            NewbieHonorReward = source.NewbieHonorReward,
            DailyResetDay = source.DailyResetDay,
            WeeklyResetWeek = source.WeeklyResetWeek
        };

        internal static NotifyLifeTreeData BuildNotifyLifeTreeData(Player player)
        {
            player.LifeTreeData ??= new();
            return player.LifeTreeData;
        }

        internal static IReadOnlyCollection<int> GetNavigableChapterIds() => NavigableChapters.Value;

        internal static int? GetCurrentPopupChapterId() => CurrentPopupChapter.Value;

        internal static bool NormalizePersistedChapterAcknowledgements(Player player)
        {
            player.LifeTreeData ??= new();
            NotifyLifeTreeData data = player.LifeTreeData;
            List<int> normalized = data.FinishedChapters
                .Where(NavigableChapters.Value.Contains)
                .Distinct()
                .OrderBy(chapterId => chapterId)
                .ToList();
            if (CurrentPopupChapter.Value is int chapterId
                && !Chapters.Value.ContainsKey(chapterId)
                && !normalized.Contains(chapterId))
            {
                normalized.Add(chapterId);
            }
            if (data.FinishedChapters.SequenceEqual(normalized))
                return false;

            data.FinishedChapters = normalized;
            return true;
        }

        private static int FinishProcess(NotifyLifeTreeData data, LifeTreeFinishProcessRequest request)
        {
            switch (request.Process)
            {
                case 1 when request.ProcessId is null or 0:
                    if (data.IsFinishGuide) return 20326006;
                    data.IsFinishGuide = true;
                    return 0;
                case 2 when request.ProcessId is null or 0:
                    if (data.IsFinishLifeTreePv) return 20326006;
                    data.IsFinishLifeTreePv = true;
                    return 0;
                case 3 when request.ProcessId is int chapterId && chapterId > 0:
                    if (!NavigableChapters.Value.Contains(chapterId)) return 20326002;
                    if (data.FinishedChapters.Contains(chapterId)) return 20326006;
                    data.FinishedChapters.Add(chapterId);
                    return 0;
                default:
                    return 20326007;
            }
        }

        private static int UnlockCharacter(Session session, LifeTreeUnlockCharacterRequest request, out LifeTreeUnlockCharacterData responseData)
        {
            responseData = new() { Id = request.CharacterId, UnlockStatus = request.Status };
            if (!Characters.Value.TryGetValue(request.CharacterId, out LifeTreeCharacterTable? character))
                return 20326001;
            if (request.Status < 1 || request.Status > character.ConditionIds.Count)
                return 20326003;

            NotifyLifeTreeData data = BuildNotifyLifeTreeData(session.player);
            int current = data.UnlockCharacterData.TryGetValue(request.CharacterId, out LifeTreeUnlockCharacterData? stored)
                ? stored.UnlockStatus : 0;
            if (current < 0 || current > character.ConditionIds.Count)
                return 20326008;
            if (request.Status != current + 1)
                return 20326004;
            if (!Conditions.Value.TryGetValue(character.ConditionIds[request.Status - 1], out ConditionTable? condition)
                || !ConditionSatisfied(session, condition))
                return 20326005;

            data.UnlockCharacterData[request.CharacterId] = responseData;
            return 0;
        }

        private static bool ConditionSatisfied(Session session, ConditionTable condition)
            => ConditionSatisfied(session, condition, new HashSet<int>());

        private static bool ConditionSatisfied(Session session, ConditionTable condition, HashSet<int> visiting)
        {
            if (!visiting.Add(condition.Id))
                return false;
            try
            {
                if (!string.IsNullOrWhiteSpace(condition.Formula))
                {
                    char separator = condition.Formula.Contains('|') ? '|' : '&';
                    int[] references = condition.Formula.Split(separator, StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => int.TryParse(value, out int id) ? id : 0).ToArray();
                    if (references.Length == 0 || references.Any(id => id == 0 || !Conditions.Value.ContainsKey(id)))
                        return false;
                    return separator == '|'
                        ? references.Any(id => ConditionSatisfied(session, Conditions.Value[id], visiting))
                        : references.All(id => ConditionSatisfied(session, Conditions.Value[id], visiting));
                }
                if (condition.Params.Count == 0)
                    return false;
                return condition.Type switch
                {
                    10105 => condition.Params.All(value =>
                        session.stage.Stages.TryGetValue((uint)value, out StageDatum? stage) && stage.Passed),
                    10102 => condition.Params.All(value =>
                        session.character.Characters.Any(character => character.Id == (uint)value)),
                    _ => false
                };
            }
            finally
            {
                visiting.Remove(condition.Id);
            }
        }
    }
}
