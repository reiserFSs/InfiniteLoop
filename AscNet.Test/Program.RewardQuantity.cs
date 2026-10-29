using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test
{
    internal partial class Program
    {
        // Client XRewardManager.GetRewardList iterates every SubIds entry (duplicates included) and shows RewardGoods.Count copies.
        private static void ValidateRewardQuantities()
        {
            using MongoCollectionOverride noOpStages = MongoCollectionOverride.InstallNoOpStageCollection();
            Type handler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
            Type grantType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardGrant");
            MethodInfo apply = handler.GetMethod("ApplyRewardsOnceAndPersist", BindingFlags.Static | BindingFlags.Public)!;
            MethodInfo getGoods = handler.GetMethod("GetRewardGoods", BindingFlags.Static | BindingFlags.Public)!;

            const long playerId = 99_701;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out _, out RecordingMongoCollectionProxy<Character> characterSaves, out _);
            Character character = CreateDrawCompatibilityCharacter(playerId);
            using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []), "reward-quantity");

            void Grant(string claim, int rewardId)
            {
                List<RewardGoodsTable> rows = (List<RewardGoodsTable>)getGoods.Invoke(null, [rewardId])!;
                Array grants = Array.CreateInstance(grantType, 1);
                grants.SetValue(Activator.CreateInstance(grantType, claim, rows.ToArray(), null, null), 0);
                apply.Invoke(null, [grants, harness.Session]);
            }
            int Owned(Character c, uint templateId) => c.Equips.Count(equip => equip.TemplateId == templateId);

            // Duplicate SubIds (1000000,1000001,1000001): both copies of 3042001 are granted.
            Grant("reward-quantity:dup", 100000);
            AssertEqual(2, Owned(character, 3_042_001), "reward 100000 grants both duplicate SubIds copies");
            // Equip row Count=2 (386300 = 2x 3914001).
            Grant("reward-quantity:count", 38630);
            AssertEqual(2, Owned(character, 3_914_001), "reward 38630 grants two equipment instances");
            AssertEqual(character.Equips.Count, character.Equips.Select(equip => equip.Id).Distinct().Count(),
                "reward equipment instances have distinct ids");

            // Once-only claim keys still prevent regrant.
            Grant("reward-quantity:dup", 100000);
            Grant("reward-quantity:count", 38630);
            AssertEqual(2, Owned(character, 3_042_001), "reward 100000 claim key prevents regrant");
            AssertEqual(2, Owned(character, 3_914_001), "reward 38630 claim key prevents regrant");

            // Reward without duplicate SubIds or Count>1 is unchanged: one coin row, granted once.
            RewardTable plain = TableReaderV2.Parse<RewardTable>().First(row =>
                row.SubIds.Count == 1 && ((List<RewardGoodsTable>)getGoods.Invoke(null, [row.Id])!) is [{ Count: 1 } single]
                && single.TemplateId < 1_000_000);
            int equipsBefore = character.Equips.Count;
            Grant("reward-quantity:plain", plain.Id);
            AssertEqual(equipsBefore, character.Equips.Count, "plain reward grants no equipment");
            AssertEqual(1, harness.Session.inventory.Items.Count(item => item.Count == 1 && item.Id == ((List<RewardGoodsTable>)getGoods.Invoke(null, [plain.Id])!)[0].TemplateId),
                "plain reward grants its single item once");

            // Persistence after BSON reload.
            Character reloaded = BsonSerializer.Deserialize<Character>(characterSaves.LastReplacement!.ToBson());
            AssertEqual(2, Owned(reloaded, 3_042_001), "reward 100000 copies persist after BSON reload");
            AssertEqual(2, Owned(reloaded, 3_914_001), "reward 38630 copies persist after BSON reload");
            AssertEqual(true, reloaded.AppliedRewardClaims.Contains("reward-quantity:dup", StringComparer.Ordinal), "claim key persists");
        }
    }
}
