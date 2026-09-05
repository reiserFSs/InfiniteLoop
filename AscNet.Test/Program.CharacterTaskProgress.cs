using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.task;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateCharacterTaskProgressCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> playerCollection,
                out RecordingMongoCollectionProxy<Character> characterCollection,
                out RecordingMongoCollectionProxy<Inventory> inventoryCollection);
            const long playerId = 88_061;
            CharacterTable row = TableReaderV2.Parse<CharacterTable>().First(character => character.Type == 1);
            Character character = CreateDrawCompatibilityCharacter(playerId);
            CharacterData member = character.AddCharacter(checked((uint)row.Id)).Character;
            Player player = CreateDrawCompatibilityPlayer(playerId);
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = Inventory.Coin, Count = 1_000_000 }]);
            using LoopbackSessionHarness harness = new(character, player, inventory, "character-task-progress-compat-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
            ConditionTable promotion = TableReaderV2.Parse<ConditionTable>().First(condition => condition.Type == 13206);
            ConditionTable evolution = TableReaderV2.Parse<ConditionTable>().First(condition => condition.Type == 13205);
            CurrentConditionTable coin = TableReaderV2.Parse<CurrentConditionTable>().First(condition => condition.Type == 11202 && condition.Params[1] == Inventory.Coin);
            CurrentConditionTable serum = TableReaderV2.Parse<CurrentConditionTable>().First(condition => condition.Type == 11202 && condition.Params[1] == Inventory.ActionPoint);
            int spent = 0;
            int packetId = 88_610;
            for (int count = 1; count <= 2; count++)
            {
                CharacterGradeTable grade = TableReaderV2.Parse<CharacterGradeTable>().Single(candidate => candidate.CharacterId == row.Id && candidate.Grade == member.Grade);
                if (count == 2)
                    inventory.Items.Single(item => item.Id == Inventory.Coin).Count = 7;
                spent += (int)Math.Min(grade.UseItemCount.GetValueOrDefault(),
                    inventory.Items.Single(item => item.Id == Inventory.Coin).Count);
                InvokeRegisteredRequestHandler("CharacterPromoteGradeRequest", harness.Session, ++packetId, new CharacterPromoteGradeRequest { TemplateId = row.Id });
                CharacterPromoteGradeResponse response = (CharacterPromoteGradeResponse)ReadResponsePayload(harness, packetId,
                    nameof(CharacterPromoteGradeResponse), "grade task progression", typeof(CharacterPromoteGradeResponse), maxPacketsToRead: 64);
                AssertEqual(0, response.Code, "grade promotion succeeds");
                AssertEqual(count, player.MissionProgress.ConditionCounters.GetValueOrDefault(promotion.Id), "grade promotion cumulative count");
                AssertEqual(spent, player.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "grade actual coin cost");
            }
            int rejectedGrade = member.Grade;
            InvokeRegisteredRequestHandler("CharacterPromoteGradeRequest", harness.Session, ++packetId, new CharacterPromoteGradeRequest { TemplateId = -1 });
            CharacterPromoteGradeResponse rejected = (CharacterPromoteGradeResponse)ReadResponsePayload(harness, packetId,
                nameof(CharacterPromoteGradeResponse), "unowned promotion", typeof(CharacterPromoteGradeResponse), maxPacketsToRead: 64);
            AssertEqual(20009011, rejected.Code, "unowned promotion rejected");
            AssertEqual(rejectedGrade, member.Grade, "rejected promotion preserves grade");
            AssertEqual(2, player.MissionProgress.ConditionCounters.GetValueOrDefault(promotion.Id), "rejected promotion preserves event count");
            AssertEqual(spent, player.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "rejected promotion preserves spending");

            inventory.Items.Single(item => item.Id == Inventory.Coin).Count = 1_000_000;
            member.Quality = 1;
            int qualitySpent = 0;
            for (int count = 1; count <= 2; count++)
            {
                CharacterQualityFragmentTable quality = TableReaderV2.Parse<CharacterQualityFragmentTable>().Single(candidate => candidate.Type == row.Type && candidate.Quality == member.Quality);
                member.Star = quality.StarUseCount.Count;
                spent += quality.PromoteUseCoin.GetValueOrDefault();
                qualitySpent += quality.PromoteUseCoin.GetValueOrDefault();
                InvokeRegisteredRequestHandler("CharacterPromoteQualityRequest", harness.Session, ++packetId, new CharacterPromoteQualityRequest { TemplateId = row.Id });
                CharacterPromoteQualityResponse response = (CharacterPromoteQualityResponse)ReadResponsePayload(harness, packetId,
                    nameof(CharacterPromoteQualityResponse), "quality task progression", typeof(CharacterPromoteQualityResponse), maxPacketsToRead: 64);
                AssertEqual(0, response.Code, "quality promotion succeeds");
                AssertEqual(count, player.MissionProgress.ConditionCounters.GetValueOrDefault(evolution.Id), "quality promotion cumulative count");
                AssertEqual(spent, player.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "quality actual coin cost");
            }
            InvokeRegisteredRequestHandler("CharacterPromoteQualityRequest", harness.Session, ++packetId, new CharacterPromoteQualityRequest { TemplateId = -1 });
            CharacterPromoteQualityResponse qualityRejected = (CharacterPromoteQualityResponse)ReadResponsePayload(harness, packetId,
                nameof(CharacterPromoteQualityResponse), "unowned evolution", typeof(CharacterPromoteQualityResponse), maxPacketsToRead: 64);
            AssertEqual(20009011, qualityRejected.Code, "unowned evolution rejected");
            AssertEqual(3, member.Quality, "rejected evolution preserves quality");
            AssertEqual(2, player.MissionProgress.ConditionCounters.GetValueOrDefault(evolution.Id), "rejected evolution preserves count");
            AssertEqual(spent, player.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "rejected evolution preserves spending");
            AssertEqual(0, player.MissionProgress.ConditionCounters.GetValueOrDefault(serum.Id), "coin spending never advances serum");
            Player reloaded = BsonSerializer.Deserialize<Player>(playerCollection.LastReplacement!.ToBson());
            AssertEqual(2, reloaded.MissionProgress.ConditionCounters.GetValueOrDefault(promotion.Id), "grade events survive BSON reload");
            AssertEqual(2, reloaded.MissionProgress.ConditionCounters.GetValueOrDefault(evolution.Id), "quality events survive BSON reload");
            AssertEqual(spent, reloaded.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "spent currency survives BSON reload");
            Character reloadedCharacter = BsonSerializer.Deserialize<Character>(characterCollection.LastReplacement!.ToBson());
            AssertEqual(member.Grade, reloadedCharacter.Characters.Single(candidate => candidate.Id == member.Id).Grade, "promotion state persisted");
            Inventory reloadedInventory = BsonSerializer.Deserialize<Inventory>(inventoryCollection.LastReplacement!.ToBson());
            AssertEqual(1_000_000L - qualitySpent, reloadedInventory.Items.Single(item => item.Id == Inventory.Coin).Count, "quality cost persisted");
        }
    }
}
