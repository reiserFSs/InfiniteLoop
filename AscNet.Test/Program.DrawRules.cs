using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private sealed class DrawFixedRandom(double value, bool upper = false) : Random
    {
        public override double NextDouble() => value;
        public override int Next(int maxValue) => upper ? maxValue - 1 : 0;
        public override int Next(int minValue, int maxValue) => upper ? maxValue - 1 : minValue;
    }

    private static void ValidateDrawRules()
    {
        Type manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        object? Call(string name, params object[] args) => manager.GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args);
        DrawInfo Template(int group) => Version47CatalogTemplates().First(x => x.GroupId == group);
        RewardGoods Roll(Player p, DrawInfo d, Random r) => (RewardGoods)Call("RollDraw", p, d, r)!;
        DrawInfo fate = Template(15), theme = Template(11), weapon = Template(4), member = Template(1), cub = Template(22);
        foreach (var (d, rate) in new[] { (fate, .015), (theme, .005), (weapon, .05), (member, .005), (cub, .0582), (Template(16), .05) })
            AssertEqual(true, Math.Abs((double)Call("RareProbability", d)! - rate) < 1e-10, $"client probability group {d.GroupId}");

        Player p = new();
        Call("GetPityRound", p, fate, new DrawFixedRandom(0));
        for (int i = 0; i < 79; i++) Roll(p, fate, new DrawFixedRandom(.999));
        AssertEqual(79, p.DrawState.PityRounds[15].Misses, "Fate misses before lower bound");
        AssertEqual(fate.ResourceIds[1], Roll(p, fate, new DrawFixedRandom(.999)).TemplateId, "Fate guarantee at 80");
        AssertEqual(0, p.DrawState.PityRounds[15].Misses, "S resets pity immediately");
        p = new();
        Call("GetPityRound", p, fate, new DrawFixedRandom(.999, true));
        for (int i = 0; i < 99; i++) Roll(p, fate, new DrawFixedRandom(.999, true));
        AssertEqual(99, p.DrawState.PityRounds[15].Misses, "Fate upper bound accepts 99 misses");
        Roll(p, fate, new DrawFixedRandom(.999, true));
        AssertEqual(0, p.DrawState.PityRounds[15].Misses, "Fate guarantee at 100");

        p = new();
        Roll(p, fate, new DrawFixedRandom(0));
        AssertEqual(0, p.DrawState.PityRounds[15].Misses, "natural first-pull S");
        for (int i = 0; i < 9; i++) Roll(p, fate, new DrawFixedRandom(.999));
        AssertEqual(9, p.DrawState.PityRounds[15].Misses, "ten-pull counts only pulls after early S");
        Roll(p, fate, new DrawFixedRandom(.999));
        AssertEqual(0, p.DrawState.PityRounds[15].LowerMisses, "ten-pull lower-rarity guarantee resets its counter");
        AssertEqual(10, p.DrawState.PityRounds[15].Misses, "A guarantee does not reset S pity");
        Roll(p, theme, new DrawFixedRandom(.999));
        AssertEqual(10, p.DrawState.PityRounds[15].Misses, "normal and Fate do not share pity");
        AssertEqual(1, p.DrawState.PityRounds[11].Misses, "normal maintains own pity");
        var saved = BsonSerializer.Deserialize<PlayerDrawState>(p.DrawState.ToBson());
        AssertEqual(p.DrawState.PityRounds[15].Limit, saved.PityRounds[15].Limit, "random threshold survives persistence");

        p = new();
        for (int i = 0; i < 39; i++) Roll(p, member, new DrawFixedRandom(.999));
        AssertEqual(40, p.DrawState.PityRounds[1].Limit, "Member first guarantee");
        Roll(p, member, new DrawFixedRandom(.999));
        AssertEqual(60, p.DrawState.PityRounds[1].Limit, "Member subsequent guarantee");

        foreach (DrawInfo d in new[] { weapon, Template(12), Template(13) })
        {
            p = new();
            Roll(p, d, new DrawFixedRandom(.999));
            var round = p.DrawState.PityRounds[d.GroupId];
            round.Misses = round.Limit - 1;
            AssertEqual(false, Roll(p, d, new DrawFixedRandom(.999)).TemplateId == d.ResourceIds[1], "forced off-target");
            AssertEqual(true, round.GuaranteedTarget, "calibration recorded");
            p.DrawState = BsonSerializer.Deserialize<PlayerDrawState>(p.DrawState.ToBson());
            var changed = Version47CatalogTemplates().First(x => x.GroupId == d.GroupId && x.Id != d.Id);
            round = p.DrawState.PityRounds[d.GroupId];
            round.Misses = round.Limit - 1;
            AssertEqual(changed.ResourceIds[1], Roll(p, changed, new DrawFixedRandom(.999)).TemplateId, "calibration follows target switch and reload");
            AssertEqual(false, round.GuaranteedTarget, "calibration consumed");
        }

        foreach ((int drawId, bool permitsOffTarget) in new[] { (4001, true), (7002, true), (7069, false) })
        {
            DrawInfo banner = Version47CatalogTemplates().Single(x => x.Id == drawId);
            int[] rarePool = (int[])Call("RarePool", banner)!;
            p = new();
            var round = (PlayerDrawPityRound)Call("GetPityRound", p, banner, new DrawFixedRandom(0))!;
            round.Misses = round.Limit - 1;
            AssertEqual(banner.ResourceIds[1], Roll(p, banner, new DrawFixedRandom(0)).TemplateId,
                $"banner {drawId} deterministic target");
            p = new();
            round = (PlayerDrawPityRound)Call("GetPityRound", p, banner, new DrawFixedRandom(.999, true))!;
            round.Misses = round.Limit - 1;
            int upperResult = Roll(p, banner, new DrawFixedRandom(.999, true)).TemplateId;
            AssertEqual(permitsOffTarget, upperResult != banner.ResourceIds[1],
                $"banner {drawId} per-banner target rate");
            AssertEqual(true, rarePool.Contains(upperResult), $"banner {drawId} result remains in rare pool");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _))
        {
            DrawGroupInfo active = ((List<DrawGroupInfo>)Call("GetDrawGroupInfos", new Player())!).First(x => x.Id != 1);
            long uid = 47_190;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, []), "draw-pity-initialization");
            const int firstPacket = 47_191;
            InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, firstPacket,
                new DrawGetDrawInfoListRequest { GroupId = active.Id });
            DrawGetDrawInfoListResponse first = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, firstPacket,
                nameof(DrawGetDrawInfoListResponse), "initial durable draw pity");
            Player reloaded = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Draw info read did not persist newly sampled pity."));
            int firstLimit = first.DrawInfoList.First().MaxBottomTimes;
            AssertEqual(firstLimit, reloaded.DrawState.PityRounds[active.Id].Limit, "catalog response persisted sampled pity");
            harness.Session.player = reloaded;
            const int secondPacket = 47_192;
            InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, secondPacket,
                new DrawGetDrawInfoListRequest { GroupId = active.Id });
            DrawGetDrawInfoListResponse second = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, secondPacket,
                nameof(DrawGetDrawInfoListResponse), "reloaded durable draw pity");
            AssertEqual(firstLimit, second.DrawInfoList.First().MaxBottomTimes, "reload preserves sampled pity");
        }

        p = new();
        Random random = new(7419);
        int rare = 0;
        const int attempts = 200000;
        for (int i = 0; i < attempts; i++)
        {
            if (Roll(p, fate, random).TemplateId == fate.ResourceIds[1]) rare++;
        }
        double fateOverall = (double)Call("OverallRareProbability", fate)!;
        double normalOverall = (double)Call("OverallRareProbability", theme)!;
        AssertEqual(true, Math.Abs(fateOverall - normalOverall) < 1e-12,
            $"Fate combined rate matches normal ({fateOverall:P8})");
        double simulatedOverall = (double)rare / attempts;
        AssertEqual(true, Math.Abs(simulatedOverall - normalOverall) < .0015,
            $"Fate continuous-pity simulation ({rare}/{attempts}, {simulatedOverall:P4})");
        // All currently advertised pools can produce both rare and non-rare outcomes.
        var groups = (List<DrawGroupInfo>)Call("GetDrawGroupInfos", new Player())!;
        foreach (var group in groups.Where(x => x.Id != 1))
        {
            var draws = (List<DrawInfo>)Call("GetDrawInfosByGroup", group.Id, new Player())!;
            foreach (var d in draws)
            {
                Roll(new Player(), d, new DrawFixedRandom(0));
                for (int i = 0; i < 20; i++) Roll(new Player(), d, new DrawFixedRandom((i + .5) / 20));
            }
        }
        Console.WriteLine($"Draw rules passed; Fate combined-rate sample {rare}/{attempts} ({simulatedOverall:P4}).");
    }
}
