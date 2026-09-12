using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test
{
    internal partial class Program
    {
        // FashionGetSuitRewardRequest regression. Source-driven: complete/rewarded FashionSuit rows and
        // their Reward/RewardGoods nameplate mappings come from the imported tables, never literals.
        // Covers: two distinct complete suits (one across locked-but-owned fashions), exact nameplate
        // grant, committed NotifyLogin.FashionSuitList markers, duplicate no-grant, invalid/incomplete/
        // not-gathered rejections, and the inventory-then-character persistence boundary with retry.
        private static void ValidateFashionSuitRewardCompatibility()
        {
            const string requestName = nameof(FashionGetSuitRewardRequest);
            const string responseName = nameof(FashionGetSuitRewardResponse);
            const int configNotFoundCode = 20010011;
            const int notCompleteCode = 20010012;
            const int notGatheredCode = 20010013;
            const int alreadyRewardCode = 20010014;
            const int serverInternalErrorCode = 2;
            const int packetId = 19_470;
            const long playerId = 99_500;

            List<FashionSuitTable> suitRows = TableReaderV2.Parse<FashionSuitTable>();
            List<RewardGoodsTable> rewardGoodsRows = TableReaderV2.Parse<RewardGoodsTable>();
            FashionSuitTable[] completeSuits = suitRows
                .Where(row => string.Equals(row.IsComplete, "true", StringComparison.OrdinalIgnoreCase)
                    && row.RewardId is > 0 && row.FashionIds.Count > 0)
                .OrderBy(row => row.Id)
                .ToArray();
            if (completeSuits.Length < 2)
                throw new InvalidDataException($"{requestName}: FashionSuit.tsv needs two complete rewarded suits.");
            FashionSuitTable firstSuit = completeSuits[0];
            FashionSuitTable secondSuit = completeSuits.First(row => row.RewardId != firstSuit.RewardId);
            List<RewardGoodsTable> firstGoods = ResolveRewardGoods(
                firstSuit.RewardId!.Value, rewardGoodsRows, $"{requestName} suit {firstSuit.Id}");
            List<RewardGoodsTable> secondGoods = ResolveRewardGoods(
                secondSuit.RewardId!.Value, rewardGoodsRows, $"{requestName} suit {secondSuit.Id}");
            if (firstGoods.Count != 1 || secondGoods.Count != 1)
                throw new InvalidDataException(
                    $"{requestName}: complete suits must map to exactly one reward good each.");
            int firstNameplateId = firstGoods[0].TemplateId;
            int secondNameplateId = secondGoods[0].TemplateId;
            if (firstNameplateId == secondNameplateId
                || AscNet.Common.Database.Character.GetNameplateConfig(firstNameplateId) is null
                || AscNet.Common.Database.Character.GetNameplateConfig(secondNameplateId) is null
                || AscNet.Common.Database.Character.GetNameplateConfig(firstNameplateId)!.Group
                    == AscNet.Common.Database.Character.GetNameplateConfig(secondNameplateId)!.Group)
            {
                throw new InvalidDataException(
                    $"{requestName}: complete suits must map to distinct configured nameplate groups.");
            }

            AscNet.Common.Database.Character character = CreateFashionSuitRewardCharacter(playerId, firstSuit, secondSuit);
            if (character.WeaponFashions.Count != 0)
                throw new InvalidDataException($"{requestName}: fixture must own no weapon fashions.");
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);

            using MongoCollectionOverride mongoOverride =
                MongoCollectionOverride.InstallForDailySignInCompatibility(
                    out _,
                    out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
                    out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventoryCollection);
            using LoopbackSessionHarness harness = new(
                character,
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                sessionId: "fashion-suit-reward-compat");

            // Success 1: first complete suit, every listed fashion owned unlocked.
            InvokeRequestHandler(harness, requestName, packetId,
                new FashionGetSuitRewardRequest { SuitId = firstSuit.Id });
            FashionGetSuitRewardResponse firstResponse = ReadResponsePayload<FashionGetSuitRewardResponse>(
                harness, packetId, responseName, $"{requestName} first suit response", maxPacketsToRead: 8);
            AssertEqual(0, firstResponse.Code, $"{responseName} first suit Code");
            AssertFashionSuitRewardGoods(firstResponse.RewardGoodsList, firstGoods,
                $"{responseName} first suit RewardGoodsList");
            AssertSingleFashionSuitNameplate(character, firstNameplateId, $"{requestName} first suit");
            AssertEqual(1, characterCollection.ReplaceOneCalls, $"{requestName} first suit character save count");
            AssertEqual(1, inventoryCollection.ReplaceOneCalls, $"{requestName} first suit inventory save count");

            // Success 2: independent complete suit whose fashions are owned but IsLock=true, no weapons.
            InvokeRequestHandler(harness, requestName, packetId + 1,
                new FashionGetSuitRewardRequest { SuitId = secondSuit.Id });
            FashionGetSuitRewardResponse secondResponse = ReadResponsePayload<FashionGetSuitRewardResponse>(
                harness, packetId + 1, responseName, $"{requestName} second suit response", maxPacketsToRead: 8);
            AssertEqual(0, secondResponse.Code, $"{responseName} second suit Code");
            AssertFashionSuitRewardGoods(secondResponse.RewardGoodsList, secondGoods,
                $"{responseName} second suit RewardGoodsList");
            AssertSingleFashionSuitNameplate(character, secondNameplateId, $"{requestName} second suit");
            AssertEqual(2, character.Nameplates.Count, $"{requestName} exactly two suit nameplates");
            AssertEqual(2, characterCollection.ReplaceOneCalls, $"{requestName} second suit character save count");
            AssertEqual(2, inventoryCollection.ReplaceOneCalls, $"{requestName} second suit inventory save count");

            // Duplicate: already-claimed suit rejects without touching either document or the nameplate.
            int characterSavesBeforeDuplicate = characterCollection.ReplaceOneCalls;
            int inventorySavesBeforeDuplicate = inventoryCollection.ReplaceOneCalls;
            NameplateData firstPlateBefore = character.Nameplates.Single(value => value.Id == firstNameplateId);
            int expBeforeDuplicate = firstPlateBefore.Exp;
            long endTimeBeforeDuplicate = firstPlateBefore.EndTime;
            long getTimeBeforeDuplicate = firstPlateBefore.GetTime;
            InvokeRequestHandler(harness, requestName, packetId + 2,
                new FashionGetSuitRewardRequest { SuitId = firstSuit.Id });
            FashionGetSuitRewardResponse duplicateResponse = ReadResponsePayload<FashionGetSuitRewardResponse>(
                harness, packetId + 2, responseName, $"{requestName} duplicate response", maxPacketsToRead: 8);
            AssertEqual(alreadyRewardCode, duplicateResponse.Code, $"{responseName} duplicate Code");
            AssertEmptyList(duplicateResponse.RewardGoodsList, $"{responseName} duplicate RewardGoodsList");
            AssertEqual(characterSavesBeforeDuplicate, characterCollection.ReplaceOneCalls,
                $"{requestName} duplicate character save count");
            AssertEqual(inventorySavesBeforeDuplicate, inventoryCollection.ReplaceOneCalls,
                $"{requestName} duplicate inventory save count");
            NameplateData firstPlateAfter = character.Nameplates.Single(value => value.Id == firstNameplateId);
            AssertEqual(expBeforeDuplicate, firstPlateAfter.Exp, $"{requestName} duplicate nameplate Exp");
            AssertEqual(endTimeBeforeDuplicate, firstPlateAfter.EndTime, $"{requestName} duplicate nameplate EndTime");
            AssertEqual(getTimeBeforeDuplicate, firstPlateAfter.GetTime, $"{requestName} duplicate nameplate GetTime");
            AssertEqual(2, character.Nameplates.Count, $"{requestName} duplicate adds no nameplate");
            AssertNoFashionSuitRewardPacket(harness, $"{requestName} duplicate");

            int rejectionPacketId = packetId + 3;
            void AssertRejected(string name, int suitId, int expectedCode, Action? prepare = null, Action? restore = null)
            {
                prepare?.Invoke();
                byte[] before = character.ToBson();
                int characterSavesBefore = characterCollection.ReplaceOneCalls;
                int inventorySavesBefore = inventoryCollection.ReplaceOneCalls;
                int rejectionId = rejectionPacketId++;
                InvokeRequestHandler(harness, requestName, rejectionId,
                    new FashionGetSuitRewardRequest { SuitId = suitId });
                FashionGetSuitRewardResponse response = ReadResponsePayload<FashionGetSuitRewardResponse>(
                    harness, rejectionId, responseName, $"{name} response", maxPacketsToRead: 8);
                AssertEqual(expectedCode, response.Code, $"{name} Code");
                AssertEmptyList(response.RewardGoodsList, $"{name} RewardGoodsList");
                AssertEqual(characterSavesBefore, characterCollection.ReplaceOneCalls, $"{name} character save count");
                AssertEqual(inventorySavesBefore, inventoryCollection.ReplaceOneCalls, $"{name} inventory save count");
                AssertEqual(Convert.ToHexString(before), Convert.ToHexString(character.ToBson()),
                    $"{name} atomic character state");
                AssertNoFashionSuitRewardPacket(harness, name);
                restore?.Invoke();
            }

            AssertRejected($"{requestName} unknown suit", suitRows.Max(row => row.Id) + 1, configNotFoundCode);

            FashionSuitTable incompleteSuit = suitRows
                .Where(row => !string.Equals(row.IsComplete, "true", StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.Id)
                .FirstOrDefault()
                ?? throw new InvalidDataException($"{requestName}: FashionSuit.tsv has no incomplete row.");
            AssertRejected($"{requestName} incomplete suit", incompleteSuit.Id, notCompleteCode);

            int omittedFashionId = firstSuit.FashionIds[^1];
            AssertRejected($"{requestName} missing owned fashion", firstSuit.Id, notGatheredCode,
                prepare: () => character.Fashions.RemoveAll(fashion => fashion.Id == omittedFashionId),
                restore: () => character.Fashions.Add(new FashionList { Id = omittedFashionId, IsLock = false }));

            // Committed claims survive a BSON relog and only fully-receipted suits reach login.
            AscNet.Common.Database.Character reloaded =
                BsonSerializer.Deserialize<AscNet.Common.Database.Character>(
                    characterCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException($"{requestName}: expected a persisted character replacement."));
            harness.Session.character = reloaded;
            NotifyLogin login = BuildFashionSuitRewardLogin(harness.Session);
            AssertIntegerList(
                new[] { firstSuit.Id, secondSuit.Id }.Order().Select(id => (long)id).ToArray(),
                login.FashionSuitList.Select(entry => (long)entry.Id).ToArray(),
                $"{requestName} login claimed suit ids");
            AssertEqual(true, login.FashionSuitList.All(entry => entry.IsReward),
                $"{requestName} login entries marked IsReward");
            JArray loginSuits = (JArray)(JObject.Parse(MessagePackSerializer.ConvertToJson(
                MessagePackSerializer.Serialize(login)))[nameof(NotifyLogin.FashionSuitList)]
                ?? throw new InvalidDataException($"{requestName}: NotifyLogin.FashionSuitList missing on the wire."));
            AssertEqual(2, loginSuits.Count, $"{requestName} login FashionSuitList count");
            foreach (JObject entryJson in loginSuits.OfType<JObject>())
            {
                AssertEqual(true,
                    entryJson.Properties().Select(property => property.Name)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .SequenceEqual(["Id", "IsReward"]),
                    $"{nameof(NotifyLogin)} FashionSuitData exact MessagePack field set");
            }

            // Persistence boundary: inventory save fails first, so nothing durable changes.
            using (MongoCollectionOverride inventoryFailureOverride =
                   MongoCollectionOverride.InstallForDailySignInCompatibility(
                       out _, out _,
                       out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> failedInventories))
            {
                long failedPlayerId = playerId + 2;
                AscNet.Common.Database.Character failedCharacter =
                    CreateFashionSuitRewardCharacter(failedPlayerId, firstSuit);
                using LoopbackSessionHarness failedHarness = new(
                    failedCharacter,
                    CreateDrawCompatibilityPlayer(failedPlayerId),
                    CreateDrawCompatibilityInventory(failedPlayerId, []),
                    sessionId: "fashion-suit-reward-inventory-failure");

                failedInventories.ThrowOnReplaceOne = true;
                InvokeRequestHandler(failedHarness, requestName, packetId + 10,
                    new FashionGetSuitRewardRequest { SuitId = firstSuit.Id });
                failedInventories.ThrowOnReplaceOne = false;
                FashionGetSuitRewardResponse inventoryFailureResponse =
                    ReadResponsePayload<FashionGetSuitRewardResponse>(
                        failedHarness, packetId + 10, responseName,
                        $"{requestName} inventory failure response", maxPacketsToRead: 8);
                AssertEqual(serverInternalErrorCode, inventoryFailureResponse.Code,
                    $"{responseName} inventory failure Code");
                AssertEmptyList(inventoryFailureResponse.RewardGoodsList,
                    $"{responseName} inventory failure RewardGoodsList");
                AssertEqual(0, failedCharacter.Nameplates.Count, $"{requestName} inventory failure grants no nameplate");
                AssertEqual(0, failedCharacter.AppliedRewardClaims.Count,
                    $"{requestName} inventory failure leaves character receipt unset");
                AssertEmptyList(failedHarness.Session.inventory.AppliedRewardClaims,
                    $"{requestName} inventory failure leaves inventory receipt unset");
                AssertEqual(0, BuildFashionSuitRewardLogin(failedHarness.Session).FashionSuitList.Count,
                    $"{requestName} inventory failure leaves no login claim marker");

                int inventorySavesBeforeRetry = failedInventories.ReplaceOneCalls;
                InvokeRequestHandler(failedHarness, requestName, packetId + 11,
                    new FashionGetSuitRewardRequest { SuitId = firstSuit.Id });
                FashionGetSuitRewardResponse inventoryRetryResponse =
                    ReadResponsePayload<FashionGetSuitRewardResponse>(
                        failedHarness, packetId + 11, responseName,
                        $"{requestName} inventory failure retry response", maxPacketsToRead: 8);
                AssertEqual(0, inventoryRetryResponse.Code, $"{responseName} inventory failure retry Code");
                AssertFashionSuitRewardGoods(inventoryRetryResponse.RewardGoodsList, firstGoods,
                    $"{responseName} inventory failure retry RewardGoodsList");
                AssertSingleFashionSuitNameplate(failedCharacter, firstNameplateId,
                    $"{requestName} inventory failure retry");
                AssertEqual(inventorySavesBeforeRetry + 1, failedInventories.ReplaceOneCalls,
                    $"{requestName} inventory failure retry persists the receipt once");
            }

            // Persistence boundary: inventory commits, character save fails => partial receipt, no marker.
            using (MongoCollectionOverride partialFailureOverride =
                   MongoCollectionOverride.InstallForDailySignInCompatibility(
                       out _,
                       out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> partialCharacters,
                       out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> partialInventories))
            {
                long partialPlayerId = playerId + 3;
                AscNet.Common.Database.Character partialCharacter =
                    CreateFashionSuitRewardCharacter(partialPlayerId, firstSuit);
                using LoopbackSessionHarness partialHarness = new(
                    partialCharacter,
                    CreateDrawCompatibilityPlayer(partialPlayerId),
                    CreateDrawCompatibilityInventory(partialPlayerId, []),
                    sessionId: "fashion-suit-reward-character-failure");

                partialCharacters.ThrowOnReplaceOne = true;
                InvokeRequestHandler(partialHarness, requestName, packetId + 20,
                    new FashionGetSuitRewardRequest { SuitId = firstSuit.Id });
                partialCharacters.ThrowOnReplaceOne = false;
                FashionGetSuitRewardResponse characterFailureResponse =
                    ReadResponsePayload<FashionGetSuitRewardResponse>(
                        partialHarness, packetId + 20, responseName,
                        $"{requestName} character failure response", maxPacketsToRead: 8);
                AssertEqual(serverInternalErrorCode, characterFailureResponse.Code,
                    $"{responseName} character failure Code");
                AssertEmptyList(characterFailureResponse.RewardGoodsList,
                    $"{responseName} character failure RewardGoodsList");
                AssertEqual(1, partialInventories.ReplaceOneCalls,
                    $"{requestName} character failure persisted the inventory receipt once");
                AssertEqual(1, partialHarness.Session.inventory.AppliedRewardClaims.Count,
                    $"{requestName} character failure keeps the inventory receipt");
                AssertEqual(0, partialHarness.Session.character.AppliedRewardClaims.Count,
                    $"{requestName} character failure leaves the character receipt unset");
                AssertEqual(0, partialHarness.Session.character.Nameplates.Count,
                    $"{requestName} character failure grants no nameplate");
                AssertEqual(0, BuildFashionSuitRewardLogin(partialHarness.Session).FashionSuitList.Count,
                    $"{requestName} partial receipt leaves no login claim marker");
                long durableGrantTime = partialHarness.Session.inventory.RewardClaimTimes.Values.Single();

                // Reload the durable inventory plus the pre-failure character BSON: still unclaimed.
                partialHarness.Session.inventory = BsonSerializer.Deserialize<AscNet.Common.Database.Inventory>(
                    partialInventories.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException($"{requestName}: expected a persisted inventory replacement."));
                partialHarness.Session.character = BsonSerializer.Deserialize<AscNet.Common.Database.Character>(
                    partialCharacter.ToBson());
                AssertEqual(0, BuildFashionSuitRewardLogin(partialHarness.Session).FashionSuitList.Count,
                    $"{requestName} reloaded partial receipt leaves no login claim marker");

                InvokeRequestHandler(partialHarness, requestName, packetId + 21,
                    new FashionGetSuitRewardRequest { SuitId = firstSuit.Id });
                FashionGetSuitRewardResponse retryResponse = ReadResponsePayload<FashionGetSuitRewardResponse>(
                    partialHarness, packetId + 21, responseName,
                    $"{requestName} character failure retry response", maxPacketsToRead: 8);
                AssertEqual(0, retryResponse.Code, $"{responseName} character failure retry Code");
                AssertFashionSuitRewardGoods(retryResponse.RewardGoodsList, firstGoods,
                    $"{responseName} character failure retry RewardGoodsList");
                AscNet.Common.Database.Character committed =
                    BsonSerializer.Deserialize<AscNet.Common.Database.Character>(
                        partialCharacters.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException($"{requestName}: retry did not persist the character."));
                AssertSingleFashionSuitNameplate(committed, firstNameplateId, $"{requestName} retry");
                AssertEqual(durableGrantTime,
                    committed.Nameplates.Single(value => value.Id == firstNameplateId).GetTime,
                    $"{requestName} retry reuses the durable nameplate grant time");
                AssertEqual(1, partialInventories.ReplaceOneCalls,
                    $"{requestName} retry reuses the persisted inventory receipt without a second save");
                AssertIntegerList([firstSuit.Id],
                    BuildFashionSuitRewardLogin(partialHarness.Session)
                        .FashionSuitList.Select(entry => (long)entry.Id).ToArray(),
                    $"{requestName} retry commits the login claim marker");
            }
        }

        private static AscNet.Common.Database.Character CreateFashionSuitRewardCharacter(
            long uid,
            params FashionSuitTable[] suits)
        {
            HashSet<int> seen = [];
            List<FashionList> fashions = [];
            foreach (FashionSuitTable suit in suits)
            {
                foreach (int fashionId in suit.FashionIds)
                {
                    if (seen.Add(fashionId))
                        fashions.Add(new FashionList { Id = fashionId, IsLock = false });
                }
            }

            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(uid);
            character.Fashions = fashions;
            return character;
        }

        private static NotifyLogin BuildFashionSuitRewardLogin(Session session)
        {
            MethodInfo buildNotifyLogin = RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            return (NotifyLogin)(buildNotifyLogin.Invoke(null, [session])
                ?? throw new InvalidDataException("BuildNotifyLogin returned null."));
        }

        private static void AssertFashionSuitRewardGoods(
            IReadOnlyList<RewardGoods> actual,
            IReadOnlyList<RewardGoodsTable> expected,
            string name)
        {
            AssertEqual(expected.Count, actual.Count, $"{name} count");
            for (int index = 0; index < expected.Count; index++)
            {
                AssertEqual(expected[index].TemplateId, actual[index].TemplateId, $"{name}[{index}] TemplateId");
                AssertEqual(expected[index].Count, actual[index].Count, $"{name}[{index}] Count");
            }
        }

        private static void AssertSingleFashionSuitNameplate(
            AscNet.Common.Database.Character character,
            int nameplateId,
            string name)
        {
            NameplateData nameplate = character.Nameplates.SingleOrDefault(value => value.Id == nameplateId)
                ?? throw new InvalidDataException($"{name}: missing nameplate {nameplateId}.");
            AssertEqual(1, character.Nameplates.Count(value => value.Id == nameplateId),
                $"{name} exactly one nameplate {nameplateId}");
            AssertEqual(true, nameplate.GetTime > 0, $"{name} nameplate acquisition time");
        }

        private static void AssertNoFashionSuitRewardPacket(LoopbackSessionHarness harness, string name)
        {
            if (harness.TryReadAvailablePacket($"{name} unexpected packet", out Packet unexpected))
                throw new InvalidDataException($"{name} emitted unexpected {unexpected.Type} packet.");
        }
    }
}
