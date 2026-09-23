using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.partner;
using AscNet.Table.V2.share.partner.leveluptemplate;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.team;
using MessagePack;
using MongoDB.Bson;
using System.Globalization;
using System.Reflection;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidatePartnerDecomposeCompatibilityCore()
        {
            using MongoCollectionOverride collections = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventoryCollection);
            PartnerData Partner(int id, int templateId, int level, int exp, int breakThrough,
                int quality, int starSchedule, int mainSkillLevel = 1, int passiveSkillLevel = 1) => new()
            {
                Id = id,
                TemplateId = templateId,
                Level = level,
                Exp = exp,
                BreakThrough = breakThrough,
                Quality = quality,
                StarSchedule = starSchedule,
                SkillList =
                [
                    new PartnerSkillData { Id = id * 10, Type = 1, Level = mainSkillLevel, IsWear = true },
                    new PartnerSkillData { Id = id * 10 + 1, Type = 2, Level = passiveSkillLevel },
                    new PartnerSkillData { Id = id * 10 + 2, Type = 2, Level = 1 },
                    new PartnerSkillData { Id = id * 10 + 3, Type = 2, Level = 1 },
                    new PartnerSkillData { Id = id * 10 + 4, Type = 2, Level = 1 },
                    new PartnerSkillData { Id = id * 10 + 5, Type = 2, Level = 1 }
                ]
            };
            AscNet.Common.Database.Character character = new()
            {
                Uid = 70_100,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners =
                [
                    Partner(701, 16_010_000, 10, 0, 0, 2, 0),
                    Partner(702, 16_030_000, 5, 15, 1, 4, 30, 2)
                ]
            };
            AscNet.Common.Database.Inventory inventory = new()
            {
                Uid = character.Uid,
                Items = [new Item { Id = Inventory.Coin, Count = 9 }]
            };
            PartnerData reviewerFixture = Partner(799, 16_010_000, 1, 0, 0, 2, 0, 2);
            Dictionary<int, long> reviewerOracle = PartnerDecomposeRewardOracle([reviewerFixture]);
            AssertEqual(150L, reviewerOracle.GetValueOrDefault(61), "float oracle base refund");
            AssertEqual(13_999L, reviewerOracle.GetValueOrDefault(Inventory.Coin), "float oracle coin refund");
            AssertEqual(41L, reviewerOracle.GetValueOrDefault(40301), "float oracle skill refund");
            Dictionary<int, long> oracleRewards = PartnerDecomposeRewardOracle(character.Partners);
            using LoopbackSessionHarness harness = new(character, inventory: inventory,
                sessionId: "partner-decompose-test");
            Dictionary<int, long> inventoryCountsBefore = inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            PartnerDecomposeRequest request = MessagePackSerializer.Deserialize<PartnerDecomposeRequest>(
                MessagePackSerializer.Serialize(new PartnerDecomposeRequest { PartnerIds = [701, 702] }));
            InvokeRequestHandler(harness, nameof(PartnerDecomposeRequest), 17_200, request);
            (NotifyItemDataList itemPush, PartnerDecomposeResponse response) =
                ReadPartnerDecomposeResult(harness, "PartnerDecomposeRequest");
            AssertEqual(0, response.Code, "PartnerDecomposeResponse code");
            Dictionary<int, long> rewards = response.RewardGoodsList.ToDictionary(reward => reward.TemplateId,
                reward => (long)reward.Count);
            AssertEqual(oracleRewards.Count, rewards.Count, "PartnerDecomposeResponse oracle reward count");
            foreach ((int itemId, long count) in oracleRewards)
                AssertEqual(count, rewards.GetValueOrDefault(itemId), $"PartnerDecomposeResponse oracle reward {itemId}");
            AssertEqual(0, character.Partners.Count, "PartnerDecomposeRequest removes partners");
            AssertEqual(rewards.Count, itemPush.ItemDataList.Count, "PartnerDecomposeRequest push reward count");
            foreach (RewardGoods reward in response.RewardGoodsList)
                AssertEqual((long)reward.Count,
                    inventory.Items.Single(item => item.Id == reward.TemplateId).Count
                        - inventoryCountsBefore.GetValueOrDefault(reward.TemplateId),
                    $"PartnerDecomposeRequest inventory delta {reward.TemplateId}");
            {
                const long conflictUid = 70_150;
                PartnerData frozenPartner = Partner(705, 16_010_000, 1, 0, 0, 2, 0);
                PartnerData starTarget = Partner(706, 16_010_000, 1, 0, 0, 2, 0);
                AscNet.Common.Database.Character failedCharacter = new()
                {
                    Uid = conflictUid,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners = [frozenPartner, starTarget]
                };
                AscNet.Common.Database.Inventory failedInventory = new()
                {
                    Uid = conflictUid,
                    Items = []
                };
                AscNet.Common.Database.Player failedPlayer = CreateDrawCompatibilityPlayer(conflictUid);
                Dictionary<int, long> expectedRecoveryRewards = PartnerDecomposeRewardOracle([frozenPartner]);
                characterCollection.LastSuccessfulReplacementBson = failedCharacter.ToBson();
                inventoryCollection.LastSuccessfulReplacementBson = failedInventory.ToBson();
                playerCollection.LastSuccessfulReplacementBson = failedPlayer.ToBson();
                playerCollection.FindResults = [];
                MethodInfo dispatch = RequiredMethod(
                    typeof(Session),
                    "InvokeRequestHandler",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    [typeof(RequestPacketHandlerDelegate), typeof(Packet.Request)]);
                RequestPacketHandlerDelegate decomposeHandler =
                    GetRegisteredRequestHandler(nameof(PartnerDecomposeRequest));
                RequestPacketHandlerDelegate starHandler =
                    GetRegisteredRequestHandler(nameof(PartnerStarActivateRequest));
                byte[]? pendingPlayerBson = null;
                byte[]? pendingCharacterBson = null;
                byte[]? pendingInventoryBson = null;

                using (LoopbackSessionHarness failed = new(
                    failedCharacter,
                    failedPlayer,
                    failedInventory,
                    "partner-decompose-dispatch-isolation"))
                {
                    int characterWritesBeforeFailure = characterCollection.ReplaceOneCalls;
                    characterCollection.ThrowOnReplaceOne = true;
                    try
                    {
                        dispatch.Invoke(failed.Session, [decomposeHandler, new Packet.Request
                        {
                            Id = 17_250,
                            Name = nameof(PartnerDecomposeRequest),
                            Content = MessagePackSerializer.Serialize(new PartnerDecomposeRequest
                            {
                                PartnerIds = [frozenPartner.Id]
                            })
                        }]);
                    }
                    finally
                    {
                        characterCollection.ThrowOnReplaceOne = false;
                    }

                    PartnerDecomposeResponse failedResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        failed,
                        17_250,
                        nameof(PartnerDecomposeResponse),
                        "dispatch isolation failed decompose response");
                    AssertEqual(1, failedResponse.Code, "dispatch isolation failed decompose code");
                    pendingPlayerBson = playerCollection.LastSuccessfulReplacementBson?.ToArray()
                        ?? throw new InvalidDataException("dispatch isolation pending Player BSON was lost.");
                    pendingCharacterBson = characterCollection.LastSuccessfulReplacementBson?.ToArray()
                        ?? throw new InvalidDataException("dispatch isolation pending Character BSON was lost.");
                    pendingInventoryBson = inventoryCollection.LastSuccessfulReplacementBson?.ToArray()
                        ?? throw new InvalidDataException("dispatch isolation baseline Inventory was not retained.");
                    string pendingClaimKey = failed.Session.player.PendingPartnerDecompose!.ClaimKey;

                    if (failed.TryReadAvailablePacket("dispatch isolation failed decompose unexpected packet", out Packet failedExtra))
                        throw new InvalidDataException(
                            $"dispatch isolation failed decompose emitted unexpected {failedExtra.Type} packet.");

                    byte[] conflictCharacterBefore = failed.Session.character.ToBson();
                    dispatch.Invoke(failed.Session, [decomposeHandler, new Packet.Request
                    {
                        Id = 17_252,
                        Name = nameof(PartnerDecomposeRequest),
                        Content = MessagePackSerializer.Serialize(new PartnerDecomposeRequest
                        {
                            PartnerIds = [starTarget.Id]
                        })
                    }]);
                    PartnerDecomposeResponse conflictResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        failed,
                        17_252,
                        nameof(PartnerDecomposeResponse),
                        "dispatch isolation conflicting decompose response");
                    AssertEqual(1, conflictResponse.Code, "dispatch isolation conflicting decompose code");
                    AssertEqual(Convert.ToHexString(conflictCharacterBefore),
                        Convert.ToHexString(failed.Session.character.ToBson()),
                        "dispatch isolation conflicting decompose leaves partner state unchanged");
                    AssertEqual(pendingClaimKey, failed.Session.player.PendingPartnerDecompose?.ClaimKey,
                        "dispatch isolation conflicting decompose retains original intent");
                    if (failed.TryReadAvailablePacket("dispatch isolation conflicting decompose unexpected packet",
                        out Packet conflictExtra))
                        throw new InvalidDataException(
                            $"dispatch isolation conflicting decompose emitted unexpected {conflictExtra.Type} packet.");

                    byte[] starCharacterBefore = failed.Session.character.ToBson();
                    dispatch.Invoke(failed.Session, [starHandler, new Packet.Request
                    {
                        Id = 17_251,
                        Name = nameof(PartnerStarActivateRequest),
                        Content = MessagePackSerializer.Serialize(new PartnerStarActivateRequest
                        {
                            PartnerId = starTarget.Id,
                            UsePartnerIdList = [frozenPartner.Id]
                        })
                    }]);
                    PartnerStarActivateResponse blockedStarResponse = ReadResponsePayload<PartnerStarActivateResponse>(
                        failed,
                        17_251,
                        nameof(PartnerStarActivateResponse),
                        "dispatch isolation blocked star response");
                    AssertEqual(1, blockedStarResponse.Code, "dispatch isolation blocked star code");
                    AssertEqual(Convert.ToHexString(starCharacterBefore),
                        Convert.ToHexString(failed.Session.character.ToBson()),
                        "dispatch isolation blocks partner mutation");
                    if (failed.TryReadAvailablePacket("dispatch isolation blocked star unexpected packet", out Packet starExtra))
                        throw new InvalidDataException(
                            $"dispatch isolation blocked star emitted unexpected {starExtra.Type} packet.");

                    dispatch.Invoke(failed.Session, [decomposeHandler, new Packet.Request
                    {
                        Id = 17_253,
                        Name = nameof(PartnerDecomposeRequest),
                        Content = MessagePackSerializer.Serialize(new PartnerDecomposeRequest
                        {
                            PartnerIds = [frozenPartner.Id]
                        })
                    }]);
                    _ = ReadItemPush(failed.ReadPacket("dispatch isolation identical decompose item push"),
                        "dispatch isolation identical decompose item push");
                    PartnerDecomposeResponse retryResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        failed, 17_253, nameof(PartnerDecomposeResponse),
                        "dispatch isolation identical decompose response");
                    AssertEqual(0, retryResponse.Code, "dispatch isolation identical decompose code");
                    Dictionary<int, long> retryRewards = retryResponse.RewardGoodsList.ToDictionary(
                        reward => reward.TemplateId, reward => (long)reward.Count);
                    AssertEqual(expectedRecoveryRewards.Count, retryRewards.Count,
                        "dispatch isolation identical decompose reward count");
                    foreach ((int itemId, long count) in expectedRecoveryRewards)
                        AssertEqual(count, retryRewards.GetValueOrDefault(itemId),
                            $"dispatch isolation identical decompose reward {itemId}");
                    AssertEqual(true, failed.Session.player.PendingPartnerDecompose is null,
                        "dispatch isolation identical decompose clears pending intent");
                    AssertEqual(false, failedCharacter.Partners.Any(partner => partner.Id == frozenPartner.Id),
                        "dispatch isolation identical decompose removes frozen partner");
                    AssertEqual(true, failedCharacter.Partners.Any(partner => partner.Id == starTarget.Id),
                        "dispatch isolation identical decompose retains unrelated partner");
                    AssertEqual(pendingClaimKey, characterCollection.LastReplacement!.AppliedRewardClaims.Single(),
                        "dispatch isolation identical decompose retains original claim");
                    if (failed.TryReadAvailablePacket("dispatch isolation identical decompose unexpected packet",
                        out Packet retryExtra))
                        throw new InvalidDataException(
                            $"dispatch isolation identical decompose emitted unexpected {retryExtra.Type} packet.");
                }

                characterCollection.LastSuccessfulReplacementBson = pendingCharacterBson;
                inventoryCollection.LastSuccessfulReplacementBson = pendingInventoryBson;
                playerCollection.LastSuccessfulReplacementBson = pendingPlayerBson;
                AscNet.Common.Database.Player reloadedPlayer =
                    ReadPartnerDecomposeReplacement(playerCollection, "dispatch isolation pending Player");
                AscNet.Common.Database.Character reloadedCharacter =
                    ReadPartnerDecomposeReplacement(characterCollection, "dispatch isolation pending Character");
                AscNet.Common.Database.Inventory reloadedInventory =
                    ReadPartnerDecomposeReplacement(inventoryCollection, "dispatch isolation pending Inventory");
                Dictionary<int, long> recoveryBefore = reloadedInventory.Items
                    .ToDictionary(item => item.Id, item => item.Count);
                using (LoopbackSessionHarness recovered = new(
                    reloadedCharacter,
                    reloadedPlayer,
                    reloadedInventory,
                    "partner-decompose-dispatch-recovery"))
                {
                    recovered.Session.stage = CreateLoginAccountCompatibilityStage(conflictUid);
                    MethodInfo buildNotifyLogin = RequiredMethod(
                        RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                        "BuildNotifyLogin",
                        BindingFlags.Static | BindingFlags.NonPublic,
                        [typeof(Session)]);
                    NotifyLogin login = (NotifyLogin?)buildNotifyLogin.Invoke(null, [recovered.Session])
                        ?? throw new InvalidDataException("dispatch isolation BuildNotifyLogin returned nil.");
                    AssertEqual(true, login.PartnerList.Any(partner => partner.Id == starTarget.Id),
                        "dispatch isolation login retains non-frozen partner");
                    AssertEqual(false, login.PartnerList.Any(partner => partner.Id == frozenPartner.Id),
                        "dispatch isolation login removes frozen partner");
                    if (recovered.TryReadAvailablePacket("dispatch isolation login unexpected push", out Packet loginExtra))
                        throw new InvalidDataException(
                            $"dispatch isolation login emitted unexpected {loginExtra.Type} packet.");

                    AscNet.Common.Database.Player convergedPlayer =
                        ReadPartnerDecomposeReplacement(playerCollection, "dispatch isolation converged Player");
                    AscNet.Common.Database.Inventory convergedInventory =
                        ReadPartnerDecomposeReplacement(inventoryCollection, "dispatch isolation converged Inventory");
                    AssertEqual(true, convergedPlayer.PendingPartnerDecompose is null,
                        "dispatch isolation login clears durable Player intent");
                    foreach ((int itemId, long count) in expectedRecoveryRewards)
                        AssertEqual(count,
                            convergedInventory.Items.Single(item => item.Id == itemId).Count
                                - recoveryBefore.GetValueOrDefault(itemId),
                            $"dispatch isolation durable login refund {itemId}");
                    byte[] recoveredInventory = reloadedInventory.ToBson();
                    _ = buildNotifyLogin.Invoke(null, [recovered.Session])
                        ?? throw new InvalidDataException("dispatch isolation repeated BuildNotifyLogin returned nil.");
                    AssertEqual(Convert.ToHexString(recoveredInventory), Convert.ToHexString(reloadedInventory.ToBson()),
                        "dispatch isolation repeated login is idempotent");
                }
            }


            AscNet.Common.Database.Character underCapCharacter = new()
            {
                Uid = 70_102,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners =
                [
                    Partner(703, 16_010_000, 10, 0, 0, 2, 0),
                    Partner(704, 16_030_000, 5, 15, 1, 4, 30, 2),
                    Partner(706, 16_010_000, 1, 0, 0, 2, 0)
                ]
            };
            AscNet.Common.Database.Inventory underCapInventory = new()
            {
                Uid = underCapCharacter.Uid,
                Items = [new Item { Id = 99_999, Count = 9 }]
            };
            Dictionary<int, long> underCapRewards = PartnerDecomposeRewardOracle(
                underCapCharacter.Partners.Take(2).ToList());
            using (LoopbackSessionHarness underCap = new(underCapCharacter, inventory: underCapInventory,
                sessionId: "partner-decompose-under-cap"))
            {
                InvokeRequestHandler(underCap, nameof(PartnerDecomposeRequest), 17_202,
                    new PartnerDecomposeRequest { PartnerIds = [703, 704] });
                (_, PartnerDecomposeResponse underCapResponse) = ReadPartnerDecomposeResult(underCap, "under-cap");
                AssertEqual(0, underCapResponse.Code, "under-cap Code");
                AssertEqual(underCapRewards.Count, underCapResponse.RewardGoodsList.Count,
                    "under-cap oracle reward count");
                foreach (RewardGoods reward in underCapResponse.RewardGoodsList)
                    AssertEqual(underCapRewards.GetValueOrDefault(reward.TemplateId), (long)reward.Count,
                        $"under-cap oracle reward {reward.TemplateId}");
                if (underCap.TryReadAvailablePacket("under-cap unexpected packet", out Packet extra))
                    throw new InvalidDataException($"under-cap sent unexpected {extra.Type} packet.");
            }
            ItemTable exactHeadroomItemTable = TableReaderV2.Parse<ItemTable>().First(item =>
                item.Id != Inventory.Coin && underCapRewards.ContainsKey(item.Id)
                && underCapRewards[item.Id] <= Inventory.GetMaxCount(item));
            int exactHeadroomItemId = exactHeadroomItemTable.Id;
            long exactHeadroomReward = underCapRewards[exactHeadroomItemId];
            AscNet.Common.Database.Character exactHeadroomCharacter = new()
            {
                Uid = 70_103,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners =
                [
                    Partner(703, 16_010_000, 10, 0, 0, 2, 0),
                    Partner(704, 16_030_000, 5, 15, 1, 4, 30, 2)
                ]
            };
            AscNet.Common.Database.Inventory exactHeadroomInventory = new()
            {
                Uid = exactHeadroomCharacter.Uid,
                Items =
                [
                    new Item
                    {
                        Id = exactHeadroomItemId,
                        Count = Inventory.GetMaxCount(exactHeadroomItemTable) - exactHeadroomReward
                    }
                ]
            };
            using (LoopbackSessionHarness exactHeadroom = new(
                exactHeadroomCharacter,
                inventory: exactHeadroomInventory,
                sessionId: "partner-decompose-exact-headroom"))
            {
                InvokeRequestHandler(exactHeadroom, nameof(PartnerDecomposeRequest), 17_203,
                    new PartnerDecomposeRequest { PartnerIds = [703, 704] });
                (_, PartnerDecomposeResponse exactHeadroomResponse) =
                    ReadPartnerDecomposeResult(exactHeadroom, "exact-headroom");
                AssertEqual(0, exactHeadroomResponse.Code, "exact-headroom Code");
                Dictionary<int, long> exactHeadroomResponseRewards = exactHeadroomResponse.RewardGoodsList
                    .ToDictionary(reward => reward.TemplateId, reward => (long)reward.Count);
                AssertEqual(underCapRewards.Count, exactHeadroomResponseRewards.Count,
                    "exact-headroom response reward count");
                foreach ((int itemId, long count) in underCapRewards)
                    AssertEqual(count, exactHeadroomResponseRewards.GetValueOrDefault(itemId),
                        $"exact-headroom response reward {itemId}");
                AssertEqual(Inventory.GetMaxCount(exactHeadroomItemTable),
                    exactHeadroomInventory.Items.Single(item => item.Id == exactHeadroomItemId).Count,
                    "exact-headroom reaches item cap");
            }


            void Reject(string name, PartnerDecomposeRequest invalid,
                Action<PartnerData>? configurePartner = null,
                Action<AscNet.Common.Database.Player>? configurePlayer = null)
            {
                PartnerData partner = Partner(703, 16_010_000, 1, 0, 0, 2, 0);
                configurePartner?.Invoke(partner);
                AscNet.Common.Database.Character rejectedCharacter = new()
                {
                    Uid = 70_101 + characterCollection.ReplaceOneCalls,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners = [partner]
                };
                AscNet.Common.Database.Inventory rejectedInventory = new()
                {
                    Uid = rejectedCharacter.Uid,
                    Items = [new Item { Id = 1, Count = 9 }]
                };
                AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(rejectedCharacter.Uid);
                configurePlayer?.Invoke(player);
                using LoopbackSessionHarness rejected = new(rejectedCharacter, player, rejectedInventory,
                    $"partner-decompose-{name}");
                byte[] characterBefore = rejectedCharacter.ToBson();
                InvokeRequestHandler(rejected, nameof(PartnerDecomposeRequest), 17_201, invalid);
                PartnerDecomposeResponse invalidResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                    rejected.ReadPacket($"{name} PartnerDecomposeResponse"), nameof(PartnerDecomposeResponse));
                AssertEqual(1, invalidResponse.Code, $"{name} Code");
                AssertEqual(Convert.ToHexString(characterBefore), Convert.ToHexString(rejectedCharacter.ToBson()),
                    $"{name} preserves partners");
            }

            Reject("empty", new PartnerDecomposeRequest { PartnerIds = [] });
            Reject("duplicate", new PartnerDecomposeRequest { PartnerIds = [703, 703] });
            Reject("missing", new PartnerDecomposeRequest { PartnerIds = [999] });
            Reject("locked", new PartnerDecomposeRequest { PartnerIds = [703] },
                partner => partner.IsLock = true);
            Reject("carried", new PartnerDecomposeRequest { PartnerIds = [703] },
                partner => partner.CharacterId = 1021001);
            Reject("team prefab", new PartnerDecomposeRequest { PartnerIds = [703] }, null,
                player => player.TeamPrefabs =
                [
                    new TeamPrefabData
                    {
                        PartnerData = new Dictionary<int, TeamPrefabPartnerData?>
                        {
                            [1] = new() { PartnerId = 703 }
                        }
                    }
                ]);
            void RejectMutation(string name, Action applyFailure, Action clearFailure,
                Action<AscNet.Common.Database.Inventory>? configureInventory = null, bool retry = true,
                IReadOnlyDictionary<int, long>? expectedRetryRewards = null, bool characterCommittedOnFailure = false)
            {
                AscNet.Common.Database.Character failedCharacter = new()
                {
                    Uid = 70_200,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners =
                    [
                        Partner(703, 16_010_000, 10, 0, 0, 2, 0),
                        Partner(704, 16_030_000, 5, 15, 1, 4, 30, 2)
                    ]
                };
                AscNet.Common.Database.Inventory failedInventory = new() { Uid = failedCharacter.Uid, Items = [] };
                configureInventory?.Invoke(failedInventory);
                characterCollection.LastSuccessfulReplacementBson = failedCharacter.ToBson();
                inventoryCollection.LastSuccessfulReplacementBson = failedInventory.ToBson();
                byte[] characterBefore = failedCharacter.ToBson();
                byte[] inventoryBefore = failedInventory.ToBson();
                using LoopbackSessionHarness failed = new(failedCharacter, inventory: failedInventory,
                    sessionId: $"partner-decompose-{name}");
                try
                {
                    applyFailure();
                    InvokeRequestHandler(failed, nameof(PartnerDecomposeRequest), 17_300,
                        new PartnerDecomposeRequest { PartnerIds = [703, 704] });
                }
                finally
                {
                    clearFailure();
                }

                PartnerDecomposeResponse rejected = ReadResponsePayload<PartnerDecomposeResponse>(
                    failed.ReadPacket($"{name} PartnerDecomposeResponse"), nameof(PartnerDecomposeResponse));
                AssertEqual(1, rejected.Code, $"{name} Code");
                if (characterCommittedOnFailure)
                {
                    AssertEqual(true, failedCharacter.Partners.All(partner => partner.Id is not (703 or 704)),
                        $"{name} commits Character removal");
                }
                else
                {
                    AssertEqual(Convert.ToHexString(characterBefore), Convert.ToHexString(failedCharacter.ToBson()),
                        $"{name} preserves Character");
                }
                AssertEqual(Convert.ToHexString(inventoryBefore), Convert.ToHexString(failedInventory.ToBson()),
                    $"{name} preserves Inventory");

                if (!retry)
                    return;
                InvokeRequestHandler(failed, nameof(PartnerDecomposeRequest), 17_400,
                    new PartnerDecomposeRequest { PartnerIds = [703, 704] });
                (_, PartnerDecomposeResponse retryResponse) = ReadPartnerDecomposeResult(failed, $"{name} retry");
                AssertEqual(0, retryResponse.Code, $"{name} retry Code");
                IReadOnlyDictionary<int, long> retryRewards = expectedRetryRewards
                    ?? throw new InvalidDataException($"{name}: missing retry reward oracle.");
                AssertEqual(retryRewards.Count, retryResponse.RewardGoodsList.Count, $"{name} retry reward count");
                foreach (RewardGoods reward in retryResponse.RewardGoodsList)
                    AssertEqual(retryRewards.GetValueOrDefault(reward.TemplateId), (long)reward.Count,
                        $"{name} retry reward {reward.TemplateId}");
            }
            void VerifyAcknowledgementLoss(string name, bool inventoryFault)
            {
                AscNet.Common.Database.Character failedCharacter = new()
                {
                    Uid = inventoryFault ? 70_202 : 70_201,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners =
                    [
                        Partner(703, 16_010_000, 10, 0, 0, 2, 0),
                        Partner(704, 16_030_000, 5, 15, 1, 4, 30, 2)
                    ]
                };
                AscNet.Common.Database.Inventory failedInventory = new()
                {
                    Uid = failedCharacter.Uid,
                    Items = []
                };
                AscNet.Common.Database.Player failedPlayer = CreateDrawCompatibilityPlayer(failedCharacter.Uid);
                Dictionary<int, long> expected = PartnerDecomposeRewardOracle(failedCharacter.Partners);
                Dictionary<int, long> before = failedInventory.Items.ToDictionary(item => item.Id, item => item.Count);
                characterCollection.LastSuccessfulReplacementBson = failedCharacter.ToBson();
                inventoryCollection.LastSuccessfulReplacementBson = failedInventory.ToBson();
                playerCollection.LastSuccessfulReplacementBson = failedPlayer.ToBson();
                using (LoopbackSessionHarness failed = new(failedCharacter, failedPlayer, failedInventory,
                    $"partner-decompose-{name}"))
                {
                    try
                    {
                        if (inventoryFault)
                            inventoryCollection.ThrowAfterReplaceOne = true;
                        else
                            characterCollection.ThrowAfterReplaceOne = true;
                        InvokeRequestHandler(failed, nameof(PartnerDecomposeRequest), 17_500,
                            new PartnerDecomposeRequest { PartnerIds = [703, 704] });
                    }
                    finally
                    {
                        characterCollection.ThrowAfterReplaceOne = false;
                        inventoryCollection.ThrowAfterReplaceOne = false;
                    }
                    PartnerDecomposeResponse response = ReadResponsePayload<PartnerDecomposeResponse>(
                        failed.ReadPacket($"{name} response"), nameof(PartnerDecomposeResponse));
                    AssertEqual(1, response.Code, $"{name} acknowledgement-loss response");
                    if (failed.TryReadAvailablePacket($"{name} unexpected packet", out Packet extra))
                        throw new InvalidDataException($"{name}: unexpected {extra.Type} packet.");
                }

                AscNet.Common.Database.Character persistedCharacter =
                    ReadPartnerDecomposeReplacement(characterCollection, $"{name} Character");
                AscNet.Common.Database.Inventory persistedInventory =
                    ReadPartnerDecomposeReplacement(inventoryCollection, $"{name} Inventory");
                AscNet.Common.Database.Player persistedPlayer =
                    ReadPartnerDecomposeReplacement(playerCollection, $"{name} Player");
                string claimKey = persistedCharacter.AppliedRewardClaims.Single();
                AssertEqual(true, persistedPlayer.PendingPartnerDecompose is not null,
                    $"{name} retains Player intent");
                using (LoopbackSessionHarness recovered = new(persistedCharacter, persistedPlayer, persistedInventory,
                    $"{name}-resume"))
                {
                    RequiredMethod(
                        RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PartnerModule"),
                        "ResumePendingPartnerDecompose",
                        BindingFlags.Static | BindingFlags.Public,
                        [typeof(Session)]).Invoke(null, [recovered.Session]);
                    if (recovered.TryReadAvailablePacket($"{name} resume push", out Packet extra))
                        throw new InvalidDataException($"{name}: resume emitted {extra.Type}.");
                }

                AscNet.Common.Database.Character convergedCharacter =
                    ReadPartnerDecomposeReplacement(characterCollection, $"{name} converged Character");
                AscNet.Common.Database.Inventory convergedInventory =
                    ReadPartnerDecomposeReplacement(inventoryCollection, $"{name} converged Inventory");
                AscNet.Common.Database.Player convergedPlayer =
                    ReadPartnerDecomposeReplacement(playerCollection, $"{name} converged Player");
                AssertEqual(true, convergedPlayer.PendingPartnerDecompose is null,
                    $"{name} clears Player intent");
                AssertEqual(true, convergedCharacter.AppliedRewardClaims.Contains(claimKey),
                    $"{name} Character claim");
                AssertEqual(true, convergedInventory.AppliedRewardClaims.Contains(claimKey),
                    $"{name} Inventory claim");
                AssertEqual(true, convergedCharacter.Partners.All(partner => partner.Id is not (703 or 704)),
                    $"{name} removes frozen partners");
                foreach ((int itemId, long count) in expected)
                    AssertEqual(count, (convergedInventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0)
                        - before.GetValueOrDefault(itemId), $"{name} refund {itemId}");
            }

            void VerifyFinalPlayerAcknowledgementLoss()
            {
                const long uid = 70_203;
                PartnerData partner = Partner(709, 16_010_000, 1, 0, 0, 2, 0);
                Dictionary<int, long> expected = PartnerDecomposeRewardOracle([partner]);
                AscNet.Common.Database.Character failedCharacter = new()
                {
                    Uid = uid,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners = [partner]
                };
                AscNet.Common.Database.Inventory failedInventory = new() { Uid = uid, Items = [] };
                AscNet.Common.Database.Player failedPlayer = CreateDrawCompatibilityPlayer(uid);
                characterCollection.LastSuccessfulReplacementBson = failedCharacter.ToBson();
                inventoryCollection.LastSuccessfulReplacementBson = failedInventory.ToBson();
                playerCollection.LastSuccessfulReplacementBson = failedPlayer.ToBson();
                playerCollection.BeforeReplaceOne = document =>
                {
                    if (document.PendingPartnerDecompose is null)
                        playerCollection.ThrowAfterReplaceOne = true;
                };
                try
                {
                    using LoopbackSessionHarness failed = new(
                        failedCharacter, failedPlayer, failedInventory,
                        "partner-decompose-final-player-ack-loss");
                    InvokeRequestHandler(failed, nameof(PartnerDecomposeRequest), 17_551,
                        new PartnerDecomposeRequest { PartnerIds = [partner.Id] });
                    PartnerDecomposeResponse response = ReadResponsePayload<PartnerDecomposeResponse>(
                        failed.ReadPacket("final Player acknowledgement loss response"),
                        nameof(PartnerDecomposeResponse));
                    AssertEqual(1, response.Code, "final Player acknowledgement loss response code");
                    if (failed.TryReadAvailablePacket("final Player acknowledgement loss unexpected packet", out Packet extra))
                        throw new InvalidDataException($"final Player acknowledgement loss emitted {extra.Type}.");
                }
                finally
                {
                    playerCollection.BeforeReplaceOne = null;
                    playerCollection.ThrowAfterReplaceOne = false;
                }

                AscNet.Common.Database.Player persistedPlayer =
                    ReadPartnerDecomposeReplacement(playerCollection, "final Player acknowledgement loss Player");
                AscNet.Common.Database.Character persistedCharacter =
                    ReadPartnerDecomposeReplacement(characterCollection, "final Player acknowledgement loss Character");
                AscNet.Common.Database.Inventory persistedInventory =
                    ReadPartnerDecomposeReplacement(inventoryCollection, "final Player acknowledgement loss Inventory");
                string claimKey = persistedCharacter.AppliedRewardClaims.Single();
                AssertEqual(true, persistedPlayer.PendingPartnerDecompose is null,
                    "final Player acknowledgement loss durably clears pending intent");
                AssertEqual(1, persistedPlayer.PartnerDecomposeCompletions?.Count ?? 0,
                    "final Player acknowledgement loss durably stores completion");
                AssertEqual(true, persistedCharacter.AppliedRewardClaims.Contains(claimKey),
                    "final Player acknowledgement loss preserves Character claim");
                AssertEqual(true, persistedInventory.AppliedRewardClaims.Contains(claimKey),
                    "final Player acknowledgement loss preserves Inventory claim");
                AssertEqual(false, persistedCharacter.Partners.Any(value => value.Id == partner.Id),
                    "final Player acknowledgement loss preserves partner removal");

                int playerWritesBeforeReplay = playerCollection.ReplaceOneCalls;
                int characterWritesBeforeReplay = characterCollection.ReplaceOneCalls;
                int inventoryWritesBeforeReplay = inventoryCollection.ReplaceOneCalls;
                using LoopbackSessionHarness replay = new(
                    persistedCharacter, persistedPlayer, persistedInventory,
                    "partner-decompose-final-player-ack-loss-replay");
                InvokeRequestHandler(replay, nameof(PartnerDecomposeRequest), 17_552,
                    new PartnerDecomposeRequest { PartnerIds = [partner.Id] });
                (NotifyItemDataList replayPush, PartnerDecomposeResponse replayResponse) =
                    ReadPartnerDecomposeResult(replay, "final Player acknowledgement loss replay");
                AssertEqual(0, replayResponse.Code, "final Player acknowledgement loss replay succeeds");
                AssertEqual(expected.Count, replayResponse.RewardGoodsList.Count,
                    "final Player acknowledgement loss replay reward count");
                AssertEqual(expected.Count, replayPush.ItemDataList.Count,
                    "final Player acknowledgement loss replay push count");
                AssertEqual(playerWritesBeforeReplay, playerCollection.ReplaceOneCalls,
                    "final Player acknowledgement loss replay does not write Player");
                AssertEqual(characterWritesBeforeReplay, characterCollection.ReplaceOneCalls,
                    "final Player acknowledgement loss replay does not write Character");
                AssertEqual(inventoryWritesBeforeReplay, inventoryCollection.ReplaceOneCalls,
                    "final Player acknowledgement loss replay does not write Inventory");

            }

            VerifyFinalPlayerAcknowledgementLoss();

            VerifyAcknowledgementLoss("character acknowledgement loss", false);
            VerifyAcknowledgementLoss("inventory acknowledgement loss", true);

            RejectMutation("character save exception",
                () => characterCollection.ThrowOnReplaceOne = true,
                () => characterCollection.ThrowOnReplaceOne = false, expectedRetryRewards: underCapRewards);
            RejectMutation("inventory save exception",
                () => inventoryCollection.ThrowOnReplaceOne = true,
                () => inventoryCollection.ThrowOnReplaceOne = false, expectedRetryRewards: underCapRewards,
                characterCommittedOnFailure: true);
            RejectMutation("character zero match",
                () => characterCollection.ReplaceOneMatchedCount = 0,
                () => characterCollection.ReplaceOneMatchedCount = 1, expectedRetryRewards: underCapRewards);
            RejectMutation("inventory zero match",
                () => inventoryCollection.ReplaceOneMatchedCount = 0,
                () => inventoryCollection.ReplaceOneMatchedCount = 1, expectedRetryRewards: underCapRewards,
                characterCommittedOnFailure: true);

            KeyValuePair<int, long> cappedItem = rewards.First(reward => reward.Key != Inventory.Coin);
            ItemTable cappedItemTable = TableReaderV2.Parse<ItemTable>().Single(item => item.Id == cappedItem.Key);
            RejectMutation("item cap", () => { }, () => { },
                configureInventory: failedInventory => failedInventory.Items =
                    [new Item { Id = cappedItem.Key, Count = Inventory.GetMaxCount(cappedItemTable) - cappedItem.Value + 1 }],
                retry: false);
            RejectMutation("money cap", () => { }, () => { },
                configureInventory: failedInventory => failedInventory.Items =
                    [new Item { Id = Inventory.Coin, Count = Inventory.MoneyItemMaxCount - rewards[Inventory.Coin] + 1 }],
                retry: false);
        }
        private static Dictionary<int, long> PartnerDecomposeRewardOracle(
            IReadOnlyList<PartnerData> partners)
        {
            Dictionary<string, string> config = TableReaderV2.Parse<ConfigTable>()
                .Where(row => row.Key is "PartnerDecomposeExpItemRebate"
                    or "PartnerDecomposeLevelBreakRebate"
                    or "PartnerDecomposeEvolutionRebate"
                    or "PartnerDecomposeSkillRebate")
                .ToDictionary(row => row.Key, row => row.Value);
            double levelRate = float.Parse(config["PartnerDecomposeLevelBreakRebate"],
                NumberStyles.Number, CultureInfo.InvariantCulture);
            double evolutionRate = float.Parse(config["PartnerDecomposeEvolutionRebate"],
                NumberStyles.Number, CultureInfo.InvariantCulture);
            double skillRate = float.Parse(config["PartnerDecomposeSkillRebate"],
                NumberStyles.Number, CultureInfo.InvariantCulture);
            Dictionary<int, ItemTable> items = TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id);
            List<(int Id, int Exp, int Coin)> expItems = config["PartnerDecomposeExpItemRebate"]
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .Where(id => items.ContainsKey(id))
                .Select(id => (Id: id, Exp: items[id].SubTypeParams.FirstOrDefault(),
                    Coin: items[id].SubTypeParams.Skip(1).FirstOrDefault()))
                .Where(item => item.Exp > 0)
                .OrderByDescending(item => item.Exp)
                .ToList();
            Dictionary<int, PartnerTable> partnerRows = TableReaderV2.Parse<PartnerTable>()
                .ToDictionary(row => row.Id);
            ILookup<int, PartnerBreakThroughTable> breakthroughs = TableReaderV2.Parse<PartnerBreakThroughTable>()
                .ToLookup(row => row.PartnerId);
            ILookup<int, PartnerQualityTable> qualities = TableReaderV2.Parse<PartnerQualityTable>()
                .ToLookup(row => row.PartnerId);
            Dictionary<int, PartnerSkillTable> skills = TableReaderV2.Parse<PartnerSkillTable>()
                .ToDictionary(row => row.PartnerId);
            Dictionary<int, double> totals = [];
            void Add(int itemId, double count)
            {
                if (itemId > 0 && count > 0 && items.ContainsKey(itemId))
                    totals[itemId] = totals.GetValueOrDefault(itemId) + count;
            }
            List<(int Level, int AllExp)> Levels(int templateId) => templateId switch
            {
                501 => TableReaderV2.Parse<PartnerLevelUpTemplate501Table>()
                    .Select(row => (row.Level, row.AllExp)).ToList(),
                502 => TableReaderV2.Parse<PartnerLevelUpTemplate502Table>()
                    .Select(row => (row.Level, row.AllExp)).ToList(),
                503 => TableReaderV2.Parse<PartnerLevelUpTemplate503Table>()
                    .Select(row => (row.Level, row.AllExp)).ToList(),
                504 => TableReaderV2.Parse<PartnerLevelUpTemplate504Table>()
                    .Select(row => (row.Level, row.AllExp)).ToList(),
                _ => []
            };

            foreach (PartnerData partner in partners)
            {
                PartnerTable partnerRow = partnerRows[partner.TemplateId];
                PartnerSkillTable skillRow = skills[partner.TemplateId];
                Add(partnerRow.DecomposeItemId, partnerRow.DecomposeItemCount);
                int breakThroughExp = 0;
                for (int breakTimes = 0; breakTimes <= partner.BreakThrough; breakTimes++)
                {
                    PartnerBreakThroughTable row = breakthroughs[partner.TemplateId]
                        .Single(value => value.BreakTimes == breakTimes);
                    List<(int Level, int AllExp)> levels = Levels(row.LevelUpTemplateId);
                    int level = breakTimes == partner.BreakThrough ? partner.Level : row.LevelLimit;
                    if (breakTimes < partner.BreakThrough)
                    {
                        breakThroughExp += levels.Single(value => value.Level == level).AllExp;
                        foreach ((int itemId, int count) in row.CostItemId.Zip(row.CostItemCount))
                            Add(itemId, count * levelRate);
                    }
                    else
                    {
                        double exp = (partner.Exp + levels.Single(value => value.Level == level).AllExp
                            + breakThroughExp) * levelRate;
                        while (true)
                        {
                            (int Id, int Exp, int Coin) item = expItems.FirstOrDefault(value => exp >= value.Exp);
                            if (item.Exp == 0)
                                break;
                            Add(item.Id, 1);
                            Add(Inventory.Coin, item.Coin);
                            exp -= item.Exp;
                        }
                    }
                }
                for (int quality = partnerRow.InitQuality + 1; quality <= partner.Quality; quality++)
                {
                    PartnerQualityTable row = qualities[partner.TemplateId]
                        .Single(value => value.Quality == quality - 1);
                    Add(row.EvolutionCostItemId ?? 0, (row.EvolutionCostItemCount ?? 0) * evolutionRate);
                }
                Add(partnerRow.ChipItemId, partner.StarSchedule * evolutionRate);
                int skillLevels = partner.SkillList.Sum(skill => skill.Level);
                for (int level = partner.SkillList.Count + 1; level <= skillLevels; level++)
                    foreach ((int itemId, int count) in skillRow.UpgradeCostItemId.Zip(skillRow.UpgradeCostItemCount))
                        Add(itemId, count * skillRate);
            }
            return totals
                .Select(entry => (entry.Key, Count: checked((long)Math.Floor(entry.Value))))
                .Where(entry => entry.Count > 0)
                .ToDictionary(entry => entry.Key, entry => entry.Count);
        }
        private static void ValidatePartnerDecomposeCompatibilitySuite()
        {
            ValidatePartnerDecomposeCompatibilityCore();
            ValidatePartnerDecomposeCompletionClaimValidation();
            ValidatePartnerDecomposeCompletionRetention();
            ValidatePartnerDecomposePendingEconomicConflicts();
            ValidatePartnerDecomposeReconnectCompatibility();
        }

        private static PartnerData CreatePartnerDecomposeRegressionPartner(int id) => new()
        {
            Id = id,
            TemplateId = 16_010_000,
            Level = 1,
            Quality = 2,
            SkillList =
            [
                new PartnerSkillData { Id = id * 10, Type = 1, Level = 1, IsWear = true },
                new PartnerSkillData { Id = id * 10 + 1, Type = 2, Level = 1 },
                new PartnerSkillData { Id = id * 10 + 2, Type = 2, Level = 1 },
                new PartnerSkillData { Id = id * 10 + 3, Type = 2, Level = 1 },
                new PartnerSkillData { Id = id * 10 + 4, Type = 2, Level = 1 },
                new PartnerSkillData { Id = id * 10 + 5, Type = 2, Level = 1 }
            ]
        };

        private static void ValidatePartnerDecomposeCompletionClaimValidation()
        {
            using MongoCollectionOverride collections = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventoryCollection);
            PartnerData partner = CreatePartnerDecomposeRegressionPartner(7_321);
            Dictionary<int, long> expected = PartnerDecomposeRewardOracle([partner]);

            foreach ((string name, bool missingCharacter) in new[]
            {
                ("missing-character-claim", true),
                ("missing-inventory-claim", false)
            })
            {
                long uid = missingCharacter ? 70_321 : 70_322;
                string claimKey = $"partner-decompose:missing-claim:{uid}";
                AscNet.Common.Database.Character character = new()
                {
                    Uid = uid,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners = [],
                    AppliedRewardClaims = missingCharacter ? [] : [claimKey]
                };
                AscNet.Common.Database.Inventory inventory = new()
                {
                    Uid = uid,
                    Items = [],
                    AppliedRewardClaims = missingCharacter ? [claimKey] : []
                };
                AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
                player.PartnerDecomposeCompletions =
                [
                    new PartnerDecomposeCompletion
                    {
                        ClaimKey = claimKey,
                        PartnerIds = [partner.Id],
                        RewardGoodsList = expected.Select(entry => new RewardGoods
                        {
                            Id = entry.Key,
                            RewardType = (int)RewardType.Item,
                            TemplateId = entry.Key,
                            Count = checked((int)entry.Value)
                        }).ToList()
                    }
                ];

                using LoopbackSessionHarness harness = new(character, player, inventory,
                    $"partner-decompose-{name}");
                int writesBefore = playerCollection.ReplaceOneCalls + characterCollection.ReplaceOneCalls
                    + inventoryCollection.ReplaceOneCalls;
                InvokeRequestHandler(
                    harness,
                    nameof(PartnerDecomposeRequest),
                    17_600 + (missingCharacter ? 0 : 1),
                    new PartnerDecomposeRequest { PartnerIds = [partner.Id] });
                PartnerDecomposeResponse response = ReadResponsePayload<PartnerDecomposeResponse>(
                    harness.ReadPacket($"{name} response"),
                    nameof(PartnerDecomposeResponse));
                AssertEqual(1, response.Code, $"{name} fails closed");
                AssertEqual(false,
                    missingCharacter
                        ? character.AppliedRewardClaims.Contains(claimKey)
                        : inventory.AppliedRewardClaims.Contains(claimKey),
                    $"{name} missing claim stays missing");
                AssertEqual(true, inventory.Items.Count == 0
                    && writesBefore == playerCollection.ReplaceOneCalls + characterCollection.ReplaceOneCalls
                        + inventoryCollection.ReplaceOneCalls,
                    $"{name} fails closed without economic or persistence mutation");
            }
        }

        private static void ValidatePartnerDecomposeCompletionRetention()
        {
            using MongoCollectionOverride collections = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventoryCollection);
            const long uid = 70_323;
            PartnerData partner = CreatePartnerDecomposeRegressionPartner(7_323);
            Dictionary<int, long> expected = PartnerDecomposeRewardOracle([partner]);
            string[] historicalClaims = Enumerable.Range(0, 16)
                .Select(index => $"partner-decompose:history:{index}")
                .ToArray();
            AscNet.Common.Database.Character character = new()
            {
                Uid = uid,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners = [partner]
            };
            AscNet.Common.Database.Inventory inventory = new() { Uid = uid, Items = [] };
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
            player.PartnerDecomposeCompletions = historicalClaims
                .Select((claimKey, index) => new PartnerDecomposeCompletion
                {
                    ClaimKey = claimKey,
                    PartnerIds = [8_000 + index],
                    RewardGoodsList =
                    [
                        new RewardGoods
                        {
                            Id = Inventory.Coin,
                            RewardType = (int)RewardType.Item,
                            TemplateId = Inventory.Coin,
                            Count = index + 1
                        }
                    ]
                })
                .ToList();

            using LoopbackSessionHarness harness = new(character, player, inventory,
                "partner-decompose-retention");
            InvokeRequestHandler(
                harness,
                nameof(PartnerDecomposeRequest),
                17_602,
                new PartnerDecomposeRequest { PartnerIds = [partner.Id] });
            (_, PartnerDecomposeResponse response) =
                ReadPartnerDecomposeResult(harness, "retention");
            AssertEqual(0, response.Code, "retention succeeds");

            AscNet.Common.Database.Player persistedPlayer =
                ReadPartnerDecomposeReplacement(playerCollection, "retention Player");
            PartnerDecomposeCompletion[] completions = persistedPlayer.PartnerDecomposeCompletions?.ToArray()
                ?? throw new InvalidDataException("retention lost completion receipts.");
            AssertEqual(16, completions.Length, "retention keeps the bounded receipt count");
            AssertEqual(true,
                completions.Select(completion => completion.ClaimKey)
                    .SequenceEqual(historicalClaims.Skip(1).Append(completions[^1].ClaimKey)),
                "retention removes only the oldest receipt");
            PartnerDecomposeCompletion newest = completions[^1];
            AssertEqual(true, newest.PartnerIds.SequenceEqual([partner.Id]),
                "retention appends the current partner receipt");
            AssertEqual(expected.Count, newest.RewardGoodsList.Count,
                "retention stores the current reward count");
            foreach ((int itemId, long count) in expected)
                AssertEqual(count,
                    newest.RewardGoodsList.Single(reward => reward.TemplateId == itemId).Count,
                    $"retention stores current reward {itemId}");
        }

        private static void ValidatePartnerDecomposePendingEconomicConflicts()
        {
            using MongoCollectionOverride collections = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventoryCollection);
            PartnerData partner = CreatePartnerDecomposeRegressionPartner(7_301);
            KeyValuePair<int, long> targetReward = PartnerDecomposeRewardOracle([partner])
                .First(entry => entry.Key != Inventory.Coin && entry.Value > 0);
            ItemTable targetTable = TableReaderV2.Parse<ItemTable>().Single(item => item.Id == targetReward.Key);
            long targetCap = Inventory.GetMaxCount(targetTable);

            void Verify(string name, bool purchase)
            {
                long uid = purchase ? 70_312 : 70_311;
                AscNet.Common.Database.Character character = new()
                {
                    Uid = uid,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners = [partner]
                };
                AscNet.Common.Database.Inventory inventory = new()
                {
                    Uid = uid,
                    Items =
                    [
                        new Item { Id = targetReward.Key, Count = targetCap - targetReward.Value },
                        new Item { Id = Inventory.Coin, Count = 1 }
                    ]
                };
                AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
                if (purchase)
                {
                    player.PendingPurchase = new PlayerPendingPurchase
                    {
                        Id = 1,
                        Count = 1,
                        PreviousBuyTimes = 0,
                        BuyTime = 1,
                        ConsumeId = Inventory.Coin,
                        ConsumeCount = 1,
                        Goods =
                        [
                            new RewardGoods
                            {
                                Id = targetReward.Key,
                                RewardType = (int)RewardType.Item,
                                TemplateId = targetReward.Key,
                                Count = 1
                            }
                        ]
                    };
                }
                else
                {
                    player.PendingItemUse = new ItemUsePendingOperation
                    {
                        ClaimKey = $"pending-item-use:{uid}",
                        ItemId = Inventory.Coin,
                        Count = 1,
                        Goods =
                        [
                            new ItemUsePendingReward
                            {
                                Id = targetReward.Key,
                                TemplateId = targetReward.Key,
                                Count = 1
                            }
                        ]
                    };
                }

                characterCollection.LastSuccessfulReplacementBson = character.ToBson();
                inventoryCollection.LastSuccessfulReplacementBson = inventory.ToBson();
                playerCollection.LastSuccessfulReplacementBson = player.ToBson();
                using LoopbackSessionHarness harness = new(character, player, inventory,
                    $"partner-decompose-{name}");
                InvokeRegisteredRequestHandler(
                    nameof(PartnerDecomposeRequest),
                    harness.Session,
                    purchase ? 17_312 : 17_311,
                    new PartnerDecomposeRequest { PartnerIds = [partner.Id] });
                PartnerDecomposeResponse rejected = ReadResponsePayload<PartnerDecomposeResponse>(
                    harness.ReadPacket($"{name} response"), nameof(PartnerDecomposeResponse));
                AssertEqual(1, rejected.Code, $"{name} rejects conflicting pending operation");
                AssertEqual(true, character.Partners.Any(value => value.Id == partner.Id),
                    $"{name} retains requested partner");
                AssertEqual(true, purchase
                    ? player.PendingPurchase is not null
                    : player.PendingItemUse is not null,
                    $"{name} retains durable pending operation");

                Type module = RequiredAscNetGameServerType(purchase
                    ? "AscNet.GameServer.Handlers.PayModule"
                    : "AscNet.GameServer.Handlers.ItemModule");
                MethodInfo resume = RequiredMethod(module,
                    purchase ? "ResumePendingPurchase" : "ResumePendingItemUse",
                    BindingFlags.Static | BindingFlags.Public,
                    [typeof(Session)]);
                _ = resume.Invoke(null, [harness.Session]);
                AssertEqual(true, purchase
                    ? player.PendingPurchase is null
                    : player.PendingItemUse is null,
                    $"{name} recovery clears pending operation");
                AssertEqual(targetCap - targetReward.Value + 1,
                    inventory.Items.Single(item => item.Id == targetReward.Key).Count,
                    $"{name} recovery preserves full reward");
            }

            Verify("pending-item-use-conflict", purchase: false);
            Verify("pending-purchase-conflict", purchase: true);
        }


        private static void ValidatePartnerDecomposeReconnectCompatibility()
        {
            MethodInfo dispatch = RequiredMethod(
                typeof(Session),
                "InvokeRequestHandler",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(RequestPacketHandlerDelegate), typeof(Packet.Request)]);

            void DispatchReconnect(
                LoopbackSessionHarness target,
                AscNet.Common.Database.Player targetPlayer,
                long targetUid,
                int packetId,
                int lastMsgSeqNo)
            {
                _ = dispatch.Invoke(target.Session,
                [
                    GetRegisteredRequestHandler(nameof(ReconnectRequest)),
                    new Packet.Request
                    {
                        Id = packetId,
                        Name = nameof(ReconnectRequest),
                        Content = MessagePackSerializer.Serialize(new ReconnectRequest
                        {
                            Token = targetPlayer.Token,
                            PlayerId = checked((uint)targetUid),
                            LastMsgSeqNo = lastMsgSeqNo
                        })
                    }
                ]);
            }

            ReconnectResponse ReadReconnectResponseSkippingPushes(LoopbackSessionHarness target, string name)
            {
                while (true)
                {
                    Packet packet = target.ReadPacket(name);
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        if (push.Name is nameof(NotifyItemDataList) or nameof(NotifyPartnerDataList))
                            throw new InvalidDataException($"{name}: reconnect emitted an unsupported {push.Name}.");
                        continue;
                    }

                    return ReadResponsePayload<ReconnectResponse>(packet, nameof(ReconnectResponse));
                }
            }
            Packet ReadItemPushSkippingUnrelated(LoopbackSessionHarness target, string name)
            {
                while (true)
                {
                    Packet packet = target.ReadPacket(name);
                    if (packet.Type != Packet.ContentType.Push)
                        throw new InvalidDataException($"{name}: expected item push before response, got {packet.Type}.");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == nameof(NotifyItemDataList))
                        return packet;
                    if (push.Name == nameof(NotifyPartnerDataList))
                        throw new InvalidDataException($"{name}: emitted unsupported {push.Name}.");
                }
            }

            NotifyPartnerDataList ReadPartnerPushSkippingUnrelated(
                LoopbackSessionHarness target,
                string name)
            {
                while (true)
                {
                    Packet packet = target.ReadPacket(name);
                    if (packet.Type != Packet.ContentType.Push)
                        throw new InvalidDataException($"{name}: expected a partner push.");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == nameof(NotifyPartnerDataList))
                        return MessagePackSerializer.Deserialize<NotifyPartnerDataList>(push.Content);
                    if (push.Name is nameof(NotifyArchivePartners) or nameof(NotifyPartnerSettings))
                        continue;
                    throw new InvalidDataException($"{name}: emitted unexpected {push.Name} push.");
                }
            }

            PartnerComposeResponse ReadPartnerComposeResponseSkippingUnrelated(
                LoopbackSessionHarness target,
                string name)
            {
                while (true)
                {
                    Packet packet = target.ReadPacket(name);
                    if (packet.Type == Packet.ContentType.Response)
                        return ReadResponsePayload<PartnerComposeResponse>(
                            packet,
                            nameof(PartnerComposeResponse));
                    if (packet.Type != Packet.ContentType.Push)
                        throw new InvalidDataException($"{name}: expected a response.");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name is nameof(NotifyArchivePartners) or nameof(NotifyPartnerSettings))
                        continue;
                    throw new InvalidDataException($"{name}: emitted unexpected {push.Name} after partner push.");
                }
            }

            {
            using MongoCollectionOverride collections = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> partialPlayerCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> partialCharacterCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> partialInventoryCollection);
            const long partialUid = 70_314;
            const int partialLastMsgSeqNo = 777;
            PartnerData partialPartner = CreatePartnerDecomposeRegressionPartner(7_303);
            Dictionary<int, long> partialExpected = PartnerDecomposeRewardOracle([partialPartner]);
            AscNet.Common.Database.Character partialCharacter = new()
            {
                Uid = partialUid,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners = [partialPartner]
            };
            AscNet.Common.Database.Inventory partialInventory = new() { Uid = partialUid, Items = [] };
            AscNet.Common.Database.Player partialPlayer = CreateDrawCompatibilityPlayer(partialUid);
            partialCharacterCollection.LastSuccessfulReplacementBson = partialCharacter.ToBson();
            partialInventoryCollection.LastSuccessfulReplacementBson = partialInventory.ToBson();
            partialPlayerCollection.LastSuccessfulReplacementBson = partialPlayer.ToBson();
            partialPlayerCollection.BeforeReplaceOne = document =>
            {
                if (document.PendingPartnerDecompose is null)
                    partialPlayerCollection.ThrowOnReplaceOne = true;
            };

            using (LoopbackSessionHarness failed = new(
                       partialCharacter,
                       partialPlayer,
                       partialInventory,
                       "partner-decompose-partial-commit"))
            {
                failed.Session.stage = CreateLoginAccountCompatibilityStage(partialUid);
                InvokeRegisteredRequestHandler(
                    nameof(PartnerDecomposeRequest),
                    failed.Session,
                    17_315,
                    new PartnerDecomposeRequest { PartnerIds = [partialPartner.Id] });
                PartnerDecomposeResponse failedResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                    failed.ReadPacket("partial partner decompose response"), nameof(PartnerDecomposeResponse));
                AssertEqual(1, failedResponse.Code, "partial partner decompose surfaces player acknowledgement failure");
            }

            partialPlayerCollection.BeforeReplaceOne = null;
            partialPlayerCollection.ThrowOnReplaceOne = false;
            AscNet.Common.Database.Player durablePartialPlayer =
                ReadPartnerDecomposeReplacement(partialPlayerCollection, "partial partner decompose Player");
            AscNet.Common.Database.Character durablePartialCharacter =
                ReadPartnerDecomposeReplacement(partialCharacterCollection, "partial partner decompose Character");
            AscNet.Common.Database.Inventory durablePartialInventory =
                ReadPartnerDecomposeReplacement(partialInventoryCollection, "partial partner decompose Inventory");
            PartnerDecomposePendingOperation durablePending =
                durablePartialPlayer.PendingPartnerDecompose
                ?? throw new InvalidDataException("partial partner decompose did not retain durable pending intent.");
            AssertEqual(false, durablePartialCharacter.Partners.Any(value => value.Id == partialPartner.Id),
                "final Player precommit retains durable Character removal");
            AssertEqual(true, durablePartialCharacter.AppliedRewardClaims.Contains(durablePending.ClaimKey),
                "final Player precommit retains durable Character claim");
            AssertEqual(true, durablePartialInventory.AppliedRewardClaims.Contains(durablePending.ClaimKey),
                "final Player precommit retains durable Inventory claim");
            foreach ((int itemId, long count) in partialExpected)
                AssertEqual(count, durablePartialInventory.Items.Single(item => item.Id == itemId).Count,
                    $"final Player precommit retains durable Inventory reward {itemId}");
            byte[] characterBeforeRetry = durablePartialCharacter.ToBson();
            byte[] inventoryBeforeRetry = durablePartialInventory.ToBson();

            using (LoopbackSessionHarness recovered = new(
                       durablePartialCharacter,
                       durablePartialPlayer,
                       durablePartialInventory,
                       "partner-decompose-partial-reconnect"))
            {
                recovered.Session.stage = CreateLoginAccountCompatibilityStage(partialUid);
                DispatchReconnect(recovered, durablePartialPlayer, partialUid, 17_316, partialLastMsgSeqNo);
                ReconnectResponse reconnectResponse =
                    ReadReconnectResponseSkippingPushes(recovered, "partial reconnect response");
                AssertEqual(0, reconnectResponse.Code, "partial reconnect response code");
                AssertEqual(partialLastMsgSeqNo, reconnectResponse.RequestNo,
                    "partial reconnect returns the client request sequence");
                AssertNoPartnerRecoveryPushes(recovered, "partial reconnect");
                AssertEqual(true, durablePartialPlayer.PendingPartnerDecompose is not null,
                    "partial reconnect leaves pending request for client replay");
                InvokeRegisteredRequestHandler(
                    nameof(PartnerDecomposeRequest),
                    recovered.Session,
                    17_317,
                    new PartnerDecomposeRequest { PartnerIds = [partialPartner.Id] });
                Packet replayItemPacket = ReadItemPushSkippingUnrelated(
                    recovered,
                    "partial reconnect replay item push");
                AssertEqual(true, replayItemPacket.No > partialLastMsgSeqNo,
                    "partial reconnect replay item push resumes after client sequence");
                PartnerDecomposeResponse replayResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                    recovered.ReadPacket("partial reconnect replay response"),
                    nameof(PartnerDecomposeResponse));
                AssertEqual(0, replayResponse.Code, "partial reconnect replay response code");
                AssertEqual(true, durablePartialPlayer.PendingPartnerDecompose is null,
                    "partial reconnect replay clears pending intent");
            }
            AscNet.Common.Database.Player completedPartialPlayer =
                ReadPartnerDecomposeReplacement(partialPlayerCollection, "final Player precommit Player");
            AscNet.Common.Database.Character completedPartialCharacter =
                ReadPartnerDecomposeReplacement(partialCharacterCollection, "final Player precommit Character");
            AscNet.Common.Database.Inventory completedPartialInventory =
                ReadPartnerDecomposeReplacement(partialInventoryCollection, "final Player precommit Inventory");
            AssertEqual(true, completedPartialPlayer.PendingPartnerDecompose is null
                && completedPartialPlayer.PartnerDecomposeCompletions?.Count == 1,
                "final Player precommit retry durably completes receipt");
            AssertEqual(Convert.ToHexString(characterBeforeRetry), Convert.ToHexString(completedPartialCharacter.ToBson()),
                "final Player precommit retry does not duplicate Character effects");
            AssertEqual(Convert.ToHexString(inventoryBeforeRetry), Convert.ToHexString(completedPartialInventory.ToBson()),
                "final Player precommit retry does not duplicate Inventory effects");

            }

            const long freshUid = 70_324;
            PartnerData freshPartner = CreatePartnerDecomposeRegressionPartner(7_304);
            Dictionary<int, long> freshExpected = PartnerDecomposeRewardOracle([freshPartner]);
            AscNet.Common.Database.Character freshCharacter = new()
            {
                Uid = freshUid,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners = [freshPartner]
            };
            AscNet.Common.Database.Inventory freshInventory = new()
            {
                Uid = freshUid,
                Items = []
            };
            Stage freshStage = CreateLoginAccountCompatibilityStage(freshUid);
            AscNet.Common.Database.Player freshPlayer = CreateDrawCompatibilityPlayer(freshUid);
            string freshClaimKey = $"partner-decompose:{freshUid}:fresh-reconnect";
            freshPlayer.PendingPartnerDecompose = new PartnerDecomposePendingOperation
            {
                ClaimKey = freshClaimKey,
                PartnerIds = [freshPartner.Id],
                RewardGoodsList = freshExpected.Select(entry => new RewardGoods
                {
                    Id = entry.Key,
                    RewardType = (int)RewardType.Item,
                    TemplateId = entry.Key,
                    Count = checked((int)entry.Value)
                }).ToList()
            };
            BsonDocument freshPlayerDocument = freshPlayer.ToBsonDocument();
            freshPlayerDocument["pending_partner_decompose"].AsBsonDocument["legacy_future_field"] = true;

            using MongoCollectionOverride reconnectCollections =
                MongoCollectionOverride.InstallForBiancaCompatibility(
                    out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                    out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                    out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventoryCollection,
                    out RecordingMongoCollectionProxy<Stage> stageCollection);

            void SeedDurableBson(
                BsonDocument playerDocument,
                AscNet.Common.Database.Character character,
                AscNet.Common.Database.Inventory inventory,
                Stage stage)
            {
                byte[] playerBson = playerDocument.ToBson();
                byte[] characterBson = character.ToBson();
                byte[] inventoryBson = inventory.ToBson();
                byte[] stageBson = stage.ToBson();
                playerCollection.LastSuccessfulReplacementBson = playerBson;
                playerCollection.FindResults =
                [
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(playerBson)
                ];
                characterCollection.LastSuccessfulReplacementBson = characterBson;
                characterCollection.FindResults =
                [
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Character>(characterBson)
                ];
                inventoryCollection.LastSuccessfulReplacementBson = inventoryBson;
                inventoryCollection.FindResults =
                [
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Inventory>(inventoryBson)
                ];
                stageCollection.LastSuccessfulReplacementBson = stageBson;
                stageCollection.FindResults =
                [
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(stageBson)
                ];
            }

            void SeedDurableState(
                AscNet.Common.Database.Player player,
                AscNet.Common.Database.Character character,
                AscNet.Common.Database.Inventory inventory,
                Stage stage) =>
                SeedDurableBson(player.ToBsonDocument(), character, inventory, stage);

            (AscNet.Common.Database.Player Player,
                AscNet.Common.Database.Character Character,
                AscNet.Common.Database.Inventory Inventory) ReloadDurableState()
            {
                return (
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                        playerCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Durable Player BSON was not captured.")),
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Character>(
                        characterCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Durable Character BSON was not captured.")),
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Inventory>(
                        inventoryCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Durable Inventory BSON was not captured.")));
            }
            Stage ReloadDurableStage()
            {
                return MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
                    stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Durable Stage BSON was not captured."));
            }

            SeedDurableBson(freshPlayerDocument, freshCharacter, freshInventory, freshStage);
            (AscNet.Common.Database.Player freshPersistedPlayer,
                AscNet.Common.Database.Character freshPersistedCharacter,
                AscNet.Common.Database.Inventory freshPersistedInventory) =
                ReloadDurableState();
            Stage freshPersistedStage = ReloadDurableStage();
            using (LoopbackSessionHarness fresh = new(
                       freshPersistedCharacter,
                       freshPersistedPlayer,
                       freshPersistedInventory,
                       "partner-decompose-fresh-reconnect"))
            {
                fresh.Session.stage = freshPersistedStage;
                DispatchReconnect(fresh, freshPersistedPlayer, freshUid, 17_319, 913);
                    ReconnectResponse reconnectResponse =
                        ReadReconnectResponseSkippingPushes(fresh, "fresh reconnect response");
                    AssertEqual(0, reconnectResponse.Code, "fresh reconnect response code");
                    AssertEqual(913, reconnectResponse.RequestNo, "fresh reconnect returns client sequence");
                    AssertEqual(freshUid, fresh.Session.player.PlayerData.Id,
                        "fresh reconnect reloads Player through registered handler");
                    AssertEqual(freshUid, fresh.Session.character.Uid,
                        "fresh reconnect reloads Character through registered handler");
                    AssertEqual(freshUid, fresh.Session.inventory.Uid,
                        "fresh reconnect reloads Inventory through registered handler");
                    AssertEqual(true, fresh.Session.player.PendingPartnerDecompose is not null,
                        "fresh reconnect reloads pending intent");
                    AssertEqual(1, fresh.Session.character.Partners.Count,
                        "fresh reconnect retains pending partner before request replay");
                    AssertNoPartnerRecoveryPushes(fresh, "fresh reconnect");

                    InvokeRegisteredRequestHandler(
                        nameof(PartnerDecomposeRequest),
                        fresh.Session,
                        17_320,
                        new PartnerDecomposeRequest { PartnerIds = [freshPartner.Id] });
                    Packet freshItemPacket = ReadItemPushSkippingUnrelated(
                        fresh,
                        "fresh reconnect replay item push");
                    AssertEqual(true, freshItemPacket.No > 913,
                        "fresh reconnect replay item push resumes after client sequence");
                    PartnerDecomposeResponse freshReplayResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        fresh.ReadPacket("fresh reconnect replay response"),
                        nameof(PartnerDecomposeResponse));
                    AssertEqual(0, freshReplayResponse.Code, "fresh reconnect replay response code");
                    AssertNoPartnerRecoveryPushes(fresh, "fresh reconnect replay");
                }

                (AscNet.Common.Database.Player persistedPlayer,
                    AscNet.Common.Database.Character persistedCharacter,
                    AscNet.Common.Database.Inventory persistedInventory) =
                    ReloadDurableState();
                AssertEqual(true, persistedPlayer.PendingPartnerDecompose is null,
                    "fresh reconnect replay clears durable pending intent");
                AssertEqual(0, persistedCharacter.Partners.Count(value => value.Id == freshPartner.Id),
                    "fresh reconnect replay durably removes partner");
                AssertEqual(1, persistedCharacter.AppliedRewardClaims.Count(
                    claim => claim == freshClaimKey),
                    "fresh reconnect replay records one durable Character claim");
                AssertEqual(1, persistedInventory.AppliedRewardClaims.Count(
                    claim => claim == freshClaimKey),
                    "fresh reconnect replay records one durable Inventory claim");
                foreach ((int itemId, long count) in freshExpected)
                    AssertEqual(count, persistedInventory.Items.Single(item => item.Id == itemId).Count,
                        $"fresh reconnect replay durable Inventory {itemId}");

            const long completedUid = 70_325;
            PartnerData completedPartner = CreatePartnerDecomposeRegressionPartner(7_305);
            PartnerData laterPartner = CreatePartnerDecomposeRegressionPartner(7_306);
            Dictionary<int, long> completedExpected = PartnerDecomposeRewardOracle([completedPartner]);
            AscNet.Common.Database.Character completedCharacter = new()
            {
                Uid = completedUid,
                Characters = [],
                Equips = [],
                Fashions = [],
                Partners = [completedPartner, laterPartner]
            };
            AscNet.Common.Database.Inventory completedInventory = new()
            {
                Uid = completedUid,
                Items = []
            };
            Stage completedStage = CreateLoginAccountCompatibilityStage(completedUid);
            AscNet.Common.Database.Player completedPlayer = CreateDrawCompatibilityPlayer(completedUid);
                SeedDurableState(completedPlayer, completedCharacter, completedInventory, completedStage);

                using (LoopbackSessionHarness completed = new(
                           completedCharacter,
                           completedPlayer,
                           completedInventory,
                           "partner-decompose-completed-response-loss"))
                {
                    InvokeRegisteredRequestHandler(
                        nameof(PartnerDecomposeRequest),
                        completed.Session,
                        17_322,
                        new PartnerDecomposeRequest { PartnerIds = [completedPartner.Id] });
                    _ = ReadItemPush(
                        completed.ReadPacket("completed response-loss discarded item push"),
                        "completed response-loss discarded item push");
                    PartnerDecomposeResponse discardedResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        completed.ReadPacket("completed response-loss discarded response"),
                        nameof(PartnerDecomposeResponse));
                    AssertEqual(0, discardedResponse.Code,
                        "completed response-loss production operation succeeds before client discard");
                }

                (AscNet.Common.Database.Player completedPersistedPlayer,
                    AscNet.Common.Database.Character completedPersistedCharacter,
                    AscNet.Common.Database.Inventory completedPersistedInventory) =
                    ReloadDurableState();
                AssertEqual(true, completedPersistedPlayer.PendingPartnerDecompose is null,
                    "completed response-loss clears durable pending intent");
                AssertEqual(1, completedPersistedPlayer.PartnerDecomposeCompletions?.Count ?? 0,
                    "completed response-loss stores one completion");
                foreach ((int itemId, long count) in completedExpected)
                    AssertEqual(count,
                        completedPersistedInventory.Items.Single(item => item.Id == itemId).Count,
                        $"completed response-loss stores reward {itemId}");
                SeedDurableState(
                    completedPersistedPlayer,
                    completedPersistedCharacter,
                    completedPersistedInventory,
                    completedStage);
                (AscNet.Common.Database.Player completedReplayPlayer,
                    AscNet.Common.Database.Character completedReplayCharacter,
                    AscNet.Common.Database.Inventory completedReplayInventory) =
                    ReloadDurableState();
                Stage completedReplayStage = ReloadDurableStage();

                using (LoopbackSessionHarness replay = new(
                           completedReplayCharacter,
                           completedReplayPlayer,
                           completedReplayInventory,
                           "partner-decompose-completed-response-loss-replay"))
                {
                    replay.Session.stage = completedReplayStage;
                    DispatchReconnect(replay, completedReplayPlayer, completedUid, 17_323, 1_024);
                    ReconnectResponse reconnectResponse =
                        ReadReconnectResponseSkippingPushes(replay, "completed response-loss reconnect");
                    AssertEqual(0, reconnectResponse.Code, "completed response-loss reconnect succeeds");
                    AssertEqual(1_024, reconnectResponse.RequestNo,
                        "completed response-loss reconnect preserves sequence");

                    InvokeRegisteredRequestHandler(
                        nameof(PartnerDecomposeRequest),
                        replay.Session,
                        17_324,
                        new PartnerDecomposeRequest { PartnerIds = [completedPartner.Id] });
                    Packet replayItemPacket = ReadItemPushSkippingUnrelated(
                        replay,
                        "completed response-loss replay item push");
                    AssertEqual(true, replayItemPacket.No > 1_024,
                        "completed response-loss replay resumes push sequence");
                    _ = ReadItemPush(replayItemPacket, "completed response-loss replay item push");
                    PartnerDecomposeResponse replayResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        replay.ReadPacket("completed response-loss replay response"),
                        nameof(PartnerDecomposeResponse));
                    AssertEqual(0, replayResponse.Code, "completed response-loss replay succeeds");
                    AssertEqual(completedExpected.Count, replayResponse.RewardGoodsList.Count,
                        "completed response-loss replay reward count");
                    foreach ((int itemId, long count) in completedExpected)
                        AssertEqual(count,
                            replayResponse.RewardGoodsList.Single(reward => reward.TemplateId == itemId).Count,
                            $"completed response-loss replay reward {itemId}");

                    InvokeRegisteredRequestHandler(
                        nameof(PartnerDecomposeRequest),
                        replay.Session,
                        17_325,
                        new PartnerDecomposeRequest { PartnerIds = [laterPartner.Id] });
                    _ = ReadItemPushSkippingUnrelated(
                        replay,
                        "completed response-loss unrelated later item push");
                    PartnerDecomposeResponse laterResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        replay.ReadPacket("completed response-loss unrelated later response"),
                        nameof(PartnerDecomposeResponse));
                    AssertEqual(0, laterResponse.Code,
                        "completed response-loss unrelated later decomposition succeeds");
                }

                (AscNet.Common.Database.Player completedAfterLaterPlayer,
                    AscNet.Common.Database.Character completedAfterLaterCharacter,
                    AscNet.Common.Database.Inventory completedAfterLaterInventory) =
                    ReloadDurableState();
                byte[] inventoryAfterLater = completedAfterLaterInventory.ToBson();
                AssertEqual(2, completedAfterLaterPlayer.PartnerDecomposeCompletions?.Count ?? 0,
                    "completed response-loss retains bounded independent completions");
                SeedDurableState(
                    completedAfterLaterPlayer,
                    completedAfterLaterCharacter,
                    completedAfterLaterInventory,
                    completedStage);
                (AscNet.Common.Database.Player repeatedPlayer,
                    AscNet.Common.Database.Character repeatedCharacter,
                    AscNet.Common.Database.Inventory repeatedInventory) =
                    ReloadDurableState();
                Stage repeatedStage = ReloadDurableStage();

                using (LoopbackSessionHarness repeated = new(
                           repeatedCharacter,
                           repeatedPlayer,
                           repeatedInventory,
                           "partner-decompose-completed-response-loss-repeat"))
                {
                    repeated.Session.stage = repeatedStage;
                    DispatchReconnect(repeated, repeatedPlayer, completedUid, 17_326, 1_025);
                    _ = ReadReconnectResponseSkippingPushes(repeated, "completed response-loss repeat reconnect");
                    InvokeRegisteredRequestHandler(
                        nameof(PartnerDecomposeRequest),
                        repeated.Session,
                        17_327,
                        new PartnerDecomposeRequest { PartnerIds = [completedPartner.Id] });
                    _ = ReadItemPushSkippingUnrelated(
                        repeated,
                        "completed response-loss repeated replay item push");
                    PartnerDecomposeResponse repeatedResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                        repeated.ReadPacket("completed response-loss repeated replay response"),
                        nameof(PartnerDecomposeResponse));
                    AssertEqual(0, repeatedResponse.Code,
                        "completed response-loss repeated fresh replay succeeds");
                    AssertNoPartnerRecoveryPushes(repeated, "completed response-loss repeated replay");
                }

                (_, _, AscNet.Common.Database.Inventory finalCompletedInventory) =
                    ReloadDurableState();
                AssertEqual(Convert.ToHexString(inventoryAfterLater),
                    Convert.ToHexString(finalCompletedInventory.ToBson()),
                    "completed response-loss repeated replay preserves Inventory exactly once");
            {
                const int reusedPartnerId = 1;
                const int templateBId = 16_030_000;
                PartnerTable templateB = TableReaderV2.Parse<PartnerTable>().Single(row => row.Id == templateBId);
                PartnerData partnerA = CreatePartnerDecomposeRegressionPartner(reusedPartnerId);
                Dictionary<int, long> expectedA = PartnerDecomposeRewardOracle([partnerA]);
                const long reusedUid = 70_326;
                AscNet.Common.Database.Character reusedCharacter = new()
                {
                    Uid = reusedUid,
                    Characters = [],
                    Equips = [],
                    Fashions = [],
                    Partners = [partnerA]
                };
                AscNet.Common.Database.Inventory reusedInventory = new()
                {
                    Uid = reusedUid,
                    Items =
                    [
                        new Item { Id = templateB.ChipItemId, Count = templateB.ChipNeedCount }
                    ]
                };
                Stage reusedStage = CreateLoginAccountCompatibilityStage(reusedUid);
                AscNet.Common.Database.Player reusedPlayer = CreateDrawCompatibilityPlayer(reusedUid);
                    SeedDurableState(reusedPlayer, reusedCharacter, reusedInventory, reusedStage);

                    using (LoopbackSessionHarness reused = new(
                               reusedCharacter,
                               reusedPlayer,
                               reusedInventory,
                               "partner-decompose-reused-id"))
                    {
                        InvokeRegisteredRequestHandler(
                            nameof(PartnerDecomposeRequest),
                            reused.Session,
                            17_328,
                            new PartnerDecomposeRequest { PartnerIds = [reusedPartnerId] });
                        (_, PartnerDecomposeResponse responseA) =
                            ReadPartnerDecomposeResult(reused, "reused-ID completion A");
                        AssertEqual(0, responseA.Code, "reused-ID completion A succeeds");
                        AssertEqual(expectedA.Count, responseA.RewardGoodsList.Count,
                            "reused-ID completion A reward count");
                        foreach ((int itemId, long count) in expectedA)
                            AssertEqual(count,
                                responseA.RewardGoodsList.Single(reward => reward.TemplateId == itemId).Count,
                                $"reused-ID completion A reward {itemId}");

                        AscNet.Common.Database.Player persistedAPlayer =
                            ReloadDurableState().Player;
                        PartnerDecomposeCompletion completionA = persistedAPlayer.PartnerDecomposeCompletions?.Single()
                            ?? throw new InvalidDataException("reused-ID completion A receipt was not persisted.");

                        InvokeRegisteredRequestHandler(
                            nameof(PartnerComposeRequest),
                            reused.Session,
                            17_329,
                            new PartnerComposeRequest { TemplateIds = [templateBId], IsOneKey = false });
                        _ = ReadItemPush(
                            reused.ReadPacket("reused-ID composition item push"),
                            "reused-ID composition item push");

                        NotifyPartnerDataList partnerPush =
                            ReadPartnerPushSkippingUnrelated(reused, "reused-ID composition partner push");
                        PartnerData partnerB = partnerPush.PartnerDataList.Single();
                        AssertEqual(reusedPartnerId, partnerB.Id, "reused-ID composition reuses instance ID");
                        AssertEqual(templateBId, partnerB.TemplateId, "reused-ID composition creates B");

                        PartnerComposeResponse composeResponse =
                            ReadPartnerComposeResponseSkippingUnrelated(
                                reused,
                                "reused-ID composition response");
                        AssertEqual(0, composeResponse.Code,
                            "reused-ID composition succeeds");

                        Dictionary<int, long> expectedB = PartnerDecomposeRewardOracle([partnerB]);

                        InvokeRegisteredRequestHandler(
                            nameof(PartnerDecomposeRequest),
                            reused.Session,
                            17_330,
                            new PartnerDecomposeRequest { PartnerIds = [reusedPartnerId] });
                        (_, PartnerDecomposeResponse responseB) =
                            ReadPartnerDecomposeResult(reused, "reused-ID completion B");
                        AssertEqual(0, responseB.Code, "reused-ID completion B succeeds as new operation");
                        AssertEqual(expectedB.Count, responseB.RewardGoodsList.Count,
                            "reused-ID completion B reward count");
                        foreach ((int itemId, long count) in expectedB)
                            AssertEqual(count,
                                responseB.RewardGoodsList.Single(reward => reward.TemplateId == itemId).Count,
                                $"reused-ID completion B reward {itemId}");

                        (AscNet.Common.Database.Player persistedBPlayer,
                            AscNet.Common.Database.Character persistedBCharacter,
                            AscNet.Common.Database.Inventory persistedBInventory) =
                            ReloadDurableState();
                        PartnerDecomposeCompletion[] completions = persistedBPlayer.PartnerDecomposeCompletions?.ToArray()
                            ?? throw new InvalidDataException("reused-ID completion B receipts were not persisted.");
                        AssertEqual(2, completions.Length,
                            "reused-ID completion B stores a second receipt");
                        AssertEqual(completionA.ClaimKey, completions[0].ClaimKey,
                            "reused-ID completion A remains first");
                        PartnerDecomposeCompletion completionB = completions[^1];
                        AssertEqual(false, completionA.ClaimKey == completionB.ClaimKey,
                            "reused-ID completion B has a new claim");
                        AssertEqual(completionB.PartnerIds.SequenceEqual([reusedPartnerId]),
                            true, "reused-ID completion B receipt identity");
                        SeedDurableState(
                            persistedBPlayer,
                            persistedBCharacter,
                            persistedBInventory,
                            reusedStage);
                        (AscNet.Common.Database.Player replayPlayer,
                            AscNet.Common.Database.Character replayCharacter,
                            AscNet.Common.Database.Inventory replayInventory) =
                            ReloadDurableState();
                        Stage replayStage = ReloadDurableStage();

                        byte[] durableCharacterBeforeReplay = replayCharacter.ToBson();
                        byte[] durableInventoryBeforeReplay = replayInventory.ToBson();
                        using (LoopbackSessionHarness replay = new(
                                   replayCharacter,
                                   replayPlayer,
                                   replayInventory,
                                   "partner-decompose-reused-id-replay"))
                        {
                            replay.Session.stage = replayStage;
                            DispatchReconnect(
                                replay,
                                replayPlayer,
                                reusedUid,
                                17_331,
                                2_048);
                            ReconnectResponse reconnectResponse =
                                ReadReconnectResponseSkippingPushes(
                                    replay,
                                    "reused-ID replay reconnect response");
                            AssertEqual(0, reconnectResponse.Code,
                                "reused-ID replay reconnect succeeds");

                            InvokeRegisteredRequestHandler(
                                nameof(PartnerDecomposeRequest),
                                replay.Session,
                                17_332,
                                new PartnerDecomposeRequest { PartnerIds = [reusedPartnerId] });
                            _ = ReadItemPushSkippingUnrelated(
                                replay,
                                "reused-ID replay item push");
                            PartnerDecomposeResponse replayResponse = ReadResponsePayload<PartnerDecomposeResponse>(
                                replay.ReadPacket("reused-ID replay response"),
                                nameof(PartnerDecomposeResponse));
                            AssertEqual(0, replayResponse.Code,
                                "reused-ID replay succeeds");
                            AssertEqual(expectedB.Count, replayResponse.RewardGoodsList.Count,
                                "reused-ID replay uses newest B reward count");
                            foreach ((int itemId, long count) in expectedB)
                                AssertEqual(count,
                                    replayResponse.RewardGoodsList.Single(reward => reward.TemplateId == itemId).Count,
                                    $"reused-ID replay uses newest B reward {itemId}");
                        }

                        (_, AscNet.Common.Database.Character afterReplayCharacter,
                            AscNet.Common.Database.Inventory afterReplayInventory) =
                            ReloadDurableState();
                        AssertEqual(Convert.ToHexString(durableCharacterBeforeReplay),
                            Convert.ToHexString(afterReplayCharacter.ToBson()),
                            "reused-ID replay preserves durable Character BSON");
                        AssertEqual(Convert.ToHexString(durableInventoryBeforeReplay),
                            Convert.ToHexString(afterReplayInventory.ToBson()),
                            "reused-ID replay preserves durable Inventory BSON");
                    }
            }
        }
        private static T ReadPartnerDecomposeReplacement<T>(
            RecordingMongoCollectionProxy<T> collection,
            string name)
            where T : class =>
            MongoDB.Bson.Serialization.BsonSerializer.Deserialize<T>(
                collection.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException($"{name} was not durably saved."));

        private static (NotifyItemDataList ItemPush, PartnerDecomposeResponse Response)
            ReadPartnerDecomposeResult(LoopbackSessionHarness harness, string name)
        {
            Packet first = harness.ReadPacket($"{name} first packet");
            Packet second = harness.ReadPacket($"{name} second packet");
            Packet push = first.Type == Packet.ContentType.Push ? first : second;
            Packet response = first.Type == Packet.ContentType.Response ? first : second;
            return (
                ReadItemPush(push, $"{name} item push"),
                ReadResponsePayload<PartnerDecomposeResponse>(
                    response,
                    nameof(PartnerDecomposeResponse)));
        }

        private static void AssertNoPartnerRecoveryPushes(LoopbackSessionHarness harness, string name)
        {
            while (harness.TryReadAvailablePacket(name, out Packet packet))
            {
                if (packet.Type != Packet.ContentType.Push)
                    throw new InvalidDataException($"{name}: unexpected {packet.Type} packet.");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                if (push.Name is nameof(NotifyItemDataList) or nameof(NotifyPartnerDataList))
                    throw new InvalidDataException($"{name}: duplicated {push.Name}.");
            }
        }
    }
}
