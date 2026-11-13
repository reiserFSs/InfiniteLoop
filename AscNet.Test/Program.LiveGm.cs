using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.guide;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.Test;

internal static partial class Program
{
    private static async Task ValidateLiveGmControl(bool itemsOnly = false)
    {
        var config = AscNet.Common.Common.config;
        if (config.Database.Host != "127.0.0.1" || config.Database.Port == 27017
            || !config.Database.Name.StartsWith("ascnet_manual_full_", StringComparison.Ordinal))
            throw new InvalidOperationException("Live GM verification requires isolated loopback MongoDB on a non-default port and an ascnet_manual_full_ database.");

        ushort mongoPort = config.Database.Port;
        string database = $"ascnet_manual_full_livegm_{Guid.NewGuid():N}";
        var mongo = new MongoClient($"mongodb://127.0.0.1:{mongoPort}/?serverSelectionTimeoutMS=3000");
        var db = mongo.GetDatabase(database);
        var players = db.GetCollection<Player>("players");
        var inventories = db.GetCollection<Inventory>("inventory");
        var stages = db.GetCollection<Stage>("stages");
        string serverConfig = Path.Combine(AppContext.BaseDirectory, "Configs", "config.json");
        byte[] originalConfig = File.ReadAllBytes(serverConfig);
        using var gameReservation = new TcpListener(IPAddress.Loopback, 0);
        using var sdkReservation = new TcpListener(IPAddress.Loopback, 0);
        gameReservation.Start();
        sdkReservation.Start();
        int gamePort = ((IPEndPoint)gameReservation.LocalEndpoint).Port;
        int sdkPort = ((IPEndPoint)sdkReservation.LocalEndpoint).Port;
        gameReservation.Stop();
        sdkReservation.Stop();
        Uri origin = new($"http://127.0.0.1:{sdkPort}");
        using HttpClient http = new() { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(8) };

        try
        {
            File.WriteAllText(serverConfig, JsonSerializer.Serialize(new
            {
                GameServer = new { Host = "127.0.0.1", Port = gamePort },
                Database = new { Host = "127.0.0.1", Port = mongoPort, Name = database },
                VerboseLevel = "Normal"
            }));
            using var server = new GmTestHost(origin, managedGm: true);
            await server.WaitReady(http, gamePort);
            AssertEqual(true, (await server.Call("hello")).TryGetProperty("serverVersion", out _), "private GM protocol handshake");

            string uid1Token = await Register(1);
            string uid2Token = await Register(2);
            using var account1 = new GmTestGame(gamePort);
            using var account2 = new GmTestGame(gamePort);
            AssertEqual((1L, 1L), await account1.Login(uid1Token), "UID 1 fresh login");
            AssertEqual((2L, 1L), await account2.Login(uid2Token), "UID 2 fresh login");
            if (itemsOnly)
            {
                await ValidateLiveGmItems(server, account1, account2, gamePort, origin, http, uid2Token, inventories);
                return;
            }
            AssertEqual((2L, 1L, true), Level(await server.Call("readLevel", new { uid = 2 })), "read live level");
            long[] stagesBefore = stages.Find(row => row.Uid == 2).Single().Stages.Keys.Order().ToArray();
            var itemsBefore = inventories.Find(row => row.Uid == 2).Single().Items.Select(item => (item.Id, item.Count)).ToArray();
            var initial = await server.Call("setLevel", new { uid = 2, level = 120 });
            AssertEqual((2L, 120L, true), Level(initial), "launcher set level confirmed");
            AssertEqual(120, (await account2.AwaitPush<NotifyPlayerLevel>()).Level, "UID 2 live level push on original connection");
            AssertEqual(120L, players.Find(row => row.PlayerData.Id == 2).Single().PlayerData.Level, "UID 2 level persisted before disconnect");
            AssertEqual(1L, players.Find(row => row.PlayerData.Id == 1).Single().PlayerData.Level, "UID 1 unchanged");
            AssertEqual(false, (await account1.Pulse()).Any(push => push.Name == nameof(NotifyPlayerLevel)), "UID 1 receives no level push");
            AssertIntegerList(stagesBefore, stages.Find(row => row.Uid == 2).Single().Stages.Keys.Order().ToArray(), "setting level leaves story stages alone");
            AssertEqual(true, itemsBefore.SequenceEqual(inventories.Find(row => row.Uid == 2).Single().Items.Select(item => (item.Id, item.Count))),
                "setting level leaves inventory alone");
            Console.WriteLine("Live GM: UID 2 went from Level 1 to 120 over the private pipe and received NotifyPlayerLevel on its existing TCP connection. UID 1, stage passes, and inventory were unchanged.");

            Task<JsonElement>[] parallel = Enumerable.Range(0, 8).Select(index => index % 2 == 0
                ? server.Call("setLevel", new { uid = 2, level = 120 })
                : server.Call("readLevel", new { uid = 1 })).ToArray();
            JsonElement[] replies = await Task.WhenAll(parallel);
            for (int index = 0; index < replies.Length; index++)
                AssertEqual(index % 2 == 0 ? (2L, 120L, true) : (1L, 1L, true), Level(replies[index]), "correlated concurrent response");
            AssertEqual(false, (await account2.Pulse()).Any(push => push.Name == nameof(NotifyPlayerLevel)), "identical set sends no duplicate push");
            AssertEqual("invalid_uid", Code(await server.Call("readLevel", new { uid = 0 })), "invalid UID");
            AssertEqual("player_not_found", Code(await server.Call("setLevel", new { uid = 999, level = 120 })), "unknown UID");
            AssertEqual("invalid_level", Code(await server.Call("setLevel", new { uid = 2, level = int.MaxValue })), "unsupported level");
            AssertEqual(2L, players.CountDocuments(_ => true), "invalid targets create no player");

            account1.Dispose();
            await WaitOffline(server, 1);
            AssertEqual((1L, 2L, false), Level(await server.Call("setLevel", new { uid = 1, level = 2 })), "existing offline player updated");
            using var login1 = new GmTestGame(gamePort);
            AssertEqual((1L, 2L), await login1.Login(uid1Token), "offline change survives login");
            using var reconnect1 = new GmTestGame(gamePort);
            var racingSet = server.Call("setLevel", new { uid = 1, level = 3 });
            var transport = await reconnect1.Exchange<ReconnectResponse>(nameof(ReconnectRequest),
                new ReconnectRequest { Token = uid1Token, PlayerId = 1 });
            AssertEqual(0, transport.Response.Code, "reconnect racing level set");
            AssertEqual((1L, 3L, true), Level(await racingSet), "racing set committed");
            var pushes = transport.Pushes.Concat(await reconnect1.Pulse()).Where(push => push.Name == nameof(NotifyPlayerLevel));
            AssertEqual(true, pushes.Any(push => MessagePackSerializer.Deserialize<NotifyPlayerLevel>(push.Content).Level == 3),
                "reconnecting client gets the committed level");

            AssertEqual(true, (await server.Call("setLevel", new { uid = 1, level = 1 })).GetProperty("player").GetProperty("requiresRelog").GetBoolean(),
                "client is informed a decrease needs full login");
            AssertEqual(1030, (await reconnect1.AwaitPush<ForceLogoutNotify>()).Code, "decrease requests relog");
            using var stale = new GmTestGame(gamePort);
            AssertEqual(1030, (await stale.Exchange<ReconnectResponse>(nameof(ReconnectRequest),
                new ReconnectRequest { Token = uid1Token, PlayerId = 1 }, fenceAfter: false)).Response.Code,
                "fast reconnect cannot keep the old higher client level");
            using var fresh1 = new GmTestGame(gamePort);
            AssertEqual((1L, 1L), await fresh1.Login(uid1Token), "full login receives decreased level");

            account2.Dispose();
            fresh1.Dispose();
            await WaitOffline(server, 1);
            await WaitOffline(server, 2);
            var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
            GuideGroupTable guide = TableReaderV2.Parse<GuideGroupTable>().First(row => row.Ignore == 0 && row.RewardId > 0
                && row.ConditionId.Count == 1 && conditions[row.ConditionId[0]].Type == 10105);
            int prerequisite = conditions[guide.ConditionId[0]].Params[0];
            foreach (int uid in new[] { 1, 2 })
            {
                Stage stage = stages.Find(row => row.Uid == uid).Single();
                stage.Stages[prerequisite] = new StageDatum { StageId = prerequisite, Passed = true, StarsMark = 7 };
                stages.ReplaceOne(row => row.Id == stage.Id, stage);
            }
            using var rejoined1 = new GmTestGame(gamePort);
            using var rejoined2 = new GmTestGame(gamePort);
            AssertEqual((1L, 1L), await rejoined1.Login(uid1Token), "UID 1 relog");
            AssertEqual((2L, 120L), await rejoined2.Login(uid2Token), "UID 2 game restart preserves level");
            var goods = RewardHandler.GetRewardGoods(guide.RewardId).Single();
            long original1 = Balance(1);
            long original2 = Balance(2);
            string dryRun = Message(await server.Call("guides", new { uid = 2, guideId = guide.Id, rewardPolicy = "grant", dryRun = true }));
            AssertEqual(true, dryRun.Contains("would complete", StringComparison.Ordinal), "single guide preview through launcher pipe");
            AssertEqual(false, players.Find(row => row.PlayerData.Id == 2).Single().PlayerData.GuideData.Contains(guide.Id), "preview writes no guide");
            string preserved = Message(await server.Call("guides", new { uid = 2, groupId = guide.GroupId, rewardPolicy = "preserve" }));
            AssertEqual(true, preserved.Contains("1 preserved", StringComparison.Ordinal), "bulk preserve leaves reward path available");
            AssertEqual(original2, Balance(2), "preserve grants nothing");
            _ = await server.Call("guides", new { uid = 2, groupId = guide.GroupId, rewardPolicy = "grant" });
            AssertEqual(guide.Id, (await rejoined2.AwaitPush<NotifyGuide>()).GuideGroupId, "bulk grant pushes guide completion");
            _ = await server.Call("guides", new { uid = 2, groupId = guide.GroupId, rewardPolicy = "grant" });
            AssertEqual(original2 + goods.Count, Balance(2), "bulk grant pays once");
            _ = await server.Call("guides", new { uid = 1, groupId = guide.GroupId, rewardPolicy = "skip" });
            AssertEqual(guide.Id, (await rejoined1.AwaitPush<NotifyGuide>()).GuideGroupId, "bulk skip pushes completion for UID 1");
            AssertEqual(original1, Balance(1), "skip leaves UID 1 balance alone");
            Console.WriteLine("Live GM: concurrent replies, invalid and offline targets, a reconnect racing a level change, and bulk preserve/grant/skip passed.");

            // Kill the real host while both players are connected. No disconnect save can make this pass.
            await server.Crash();
            using var restarted = new GmTestHost(origin, managedGm: true);
            await restarted.WaitReady(http, gamePort);
            using var afterCrash = new GmTestGame(gamePort);
            AssertEqual((2L, 120L), await afterCrash.Login(uid2Token), "level 120 survives a new server process after crash");
            AssertEqual(original2 + goods.Count, Balance(2), "guide reward survives host crash exactly once");
            await restarted.Shutdown();

            using var unmanaged = new GmTestHost(origin, managedGm: false);
            await unmanaged.WaitReady(http, gamePort);
            try
            {
                await unmanaged.Call("setLevel", new { uid = 2, level = 1 }, TimeSpan.FromMilliseconds(450));
                throw new InvalidDataException("Standalone server responded to a launcher-only command.");
            }
            catch (TimeoutException) { }
            AssertEqual(120L, players.Find(row => row.PlayerData.Id == 2).Single().PlayerData.Level, "unmanaged pipe cannot mutate level");
            await unmanaged.Shutdown();
            Console.WriteLine("Live GM: Level 120 and the guide reward survived an abrupt host restart. The unmanaged server ignored GM traffic and retained graceful shutdown.");

            long Balance(int uid) => inventories.Find(row => row.Uid == uid).Single().Items.Where(item => item.Id == goods.TemplateId).Sum(item => item.Count);
        }
        finally
        {
            File.WriteAllBytes(serverConfig, originalConfig);
            await mongo.DropDatabaseAsync(database);
        }

        async Task<string> Register(int uid)
        {
            using var register = await http.PostAsJsonAsync("/api/AscNet/register", new { Username = $"livegm-{uid}", Password = Guid.NewGuid().ToString("N") });
            JsonElement account = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account");
            AssertEqual(uid, account.GetProperty("Uid").GetInt32(), "test SDK account uid");
            string token = account.GetProperty("Token").GetString()!;
            JsonElement gate = await http.GetFromJsonAsync<JsonElement>($"/api/Login/Login?loginType=0&userId={uid}&token={Uri.EscapeDataString(token)}");
            AssertEqual(0, gate.GetProperty("code").GetInt32(), "test game gate");
            return gate.GetProperty("token").GetString()!;
        }

        static (long, long, bool) Level(JsonElement reply)
        {
            AssertEqual(true, reply.GetProperty("ok").GetBoolean(), "GM success");
            JsonElement player = reply.GetProperty("player");
            return (player.GetProperty("uid").GetInt64(), player.GetProperty("level").GetInt64(), player.GetProperty("online").GetBoolean());
        }
        static string? Code(JsonElement reply) => reply.GetProperty("code").GetString();
        static string Message(JsonElement reply) => reply.GetProperty("message").GetString()!;
        static async Task WaitOffline(GmTestHost server, int uid)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (!Level(await server.Call("readLevel", new { uid })).Item3)
                    return;
                await Task.Delay(50);
            }
            throw new InvalidDataException($"UID {uid} stayed online after closing its game socket.");
        }
    }

    private sealed class GmTestHost : IDisposable
    {
        private const string Prefix = "ASCNET-GM/1 ";
        private readonly Process child;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> calls = new();
        private readonly ConcurrentQueue<string> output = new();

        public GmTestHost(Uri origin, bool managedGm)
        {
            string runtime = RuntimeEnvironment.GetRuntimeDirectory();
            string dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            ProcessStartInfo start = new(dotnet)
            {
                WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AscNet.dll"));
            start.ArgumentList.Add("--urls");
            start.ArgumentList.Add(origin.ToString());
            start.Environment["ASCNET_MANAGED_STDIN"] = "1";
            start.Environment["ASCNET_MANAGED_GM"] = managedGm ? "1" : "0";
            start.Environment["ASCNET_GAME_BIND_ADDRESS"] = "127.0.0.1";
            start.Environment["ASCNET_PUBLIC_HTTP_ORIGIN"] = origin.ToString();
            start.Environment["ASCNET_GATE_FALLBACK_USERNAME"] = "";
            child = new Process { StartInfo = start };
            child.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not { } line)
                    return;
                if (!line.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    Record(line);
                    return;
                }
                using JsonDocument frame = JsonDocument.Parse(line[Prefix.Length..]);
                if (calls.TryRemove(frame.RootElement.GetProperty("id").GetString()!, out var call))
                    call.TrySetResult(frame.RootElement.Clone());
            };
            child.ErrorDataReceived += (_, args) => { if (args.Data is { } line) Record(line); };
            if (!child.Start())
                throw new InvalidOperationException("Could not start the real AscNet host.");
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }

        public async Task WaitReady(HttpClient http, int gamePort)
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
            try
            {
                while (!child.HasExited)
                {
                    try
                    {
                        using var ready = await http.GetAsync("/api/launcher/status", deadline.Token);
                        if (ready.IsSuccessStatusCode)
                        {
                            using var probe = new TcpClient();
                            await probe.ConnectAsync(IPAddress.Loopback, gamePort, deadline.Token);
                            return;
                        }
                    }
                    catch (HttpRequestException) { }
                    catch (SocketException) { }
                    await Task.Delay(100, deadline.Token);
                }
            }
            catch (OperationCanceledException) { }
            throw new InvalidDataException("Live GM host failed to start.\n" + string.Join("\n", output));
        }

        public async Task<JsonElement> Call(string operation, object? args = null, TimeSpan? timeout = null)
        {
            string id = Guid.NewGuid().ToString("N");
            var call = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            calls.TryAdd(id, call);
            JsonObject request = args is null ? new() : JsonSerializer.SerializeToNode(args)!.AsObject();
            request["operation"] = operation;
            request["id"] = id;
            try
            {
                lock (child)
                    child.StandardInput.WriteLine(Prefix + request.ToJsonString());
                return await call.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(12));
            }
            finally { calls.TryRemove(id, out _); }
        }

        public async Task Crash()
        {
            child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task Shutdown()
        {
            child.StandardInput.WriteLine("shutdown");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            AssertEqual(0, child.ExitCode, "graceful private shutdown");
        }

        public void Dispose()
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit(5000);
            }
            child.Dispose();
        }

        private void Record(string line)
        {
            output.Enqueue(line);
            while (output.Count > 30)
                output.TryDequeue(out _);
        }
    }

    private sealed class GmTestGame : IDisposable
    {
        private readonly TcpClient client = new();
        private readonly NetworkStream stream;
        private int requestId;

        public GmTestGame(int port)
        {
            client.Connect(IPAddress.Loopback, port);
            client.NoDelay = true;
            stream = client.GetStream();
        }

        public async Task<(long, long)> Login(string token)
        {
            var result = await Exchange<LoginResponse>(nameof(LoginRequest), new LoginRequest { Token = token });
            AssertEqual(0, result.Response.Code, "real game login response");
            var login = MessagePackSerializer.Deserialize<NotifyLogin>(result.Pushes.Single(push => push.Name == nameof(NotifyLogin)).Content);
            LoginItems = login.ItemList;
            return (login.PlayerData.Id, login.PlayerData.Level);
        }

        public IReadOnlyList<Item> LoginItems { get; private set; } = [];

        public async Task<List<Packet.Push>> Pulse() =>
            (await Exchange<HeartbeatResponse>(nameof(HeartbeatRequest), new HeartbeatRequest())).Pushes;

        public async Task<T> AwaitPush<T>()
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
            while (await PacketCodec.ReadAsync(stream, deadline.Token) is { } packet)
            {
                if (packet.Type != Packet.ContentType.Push)
                    continue;
                var push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                if (push.Name == typeof(T).Name)
                    return MessagePackSerializer.Deserialize<T>(push.Content);
            }
            throw new InvalidDataException($"Socket closed before {typeof(T).Name}.");
        }

        public async Task<(T Response, List<Packet.Push> Pushes)> Exchange<T>(string name, object request, bool fenceAfter = true) where T : class
        {
            int id = ++requestId;
            int fence = ++requestId;
            await stream.WriteAsync(LoopbackSessionHarness.SerializeClientRequestFrame(name, id, request));
            if (fenceAfter)
                await stream.WriteAsync(LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HeartbeatRequest), fence, new HeartbeatRequest()));
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            List<Packet.Push> pushes = [];
            T? response = null;
            while (await PacketCodec.ReadAsync(stream, deadline.Token) is { } packet)
            {
                if (packet.Type == Packet.ContentType.Push)
                    pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                else if (packet.Type == Packet.ContentType.Response)
                {
                    var frame = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    if (frame.Id == fence)
                        return (response ?? throw new InvalidDataException($"Missing {typeof(T).Name}."), pushes);
                    AssertEqual((id, typeof(T).Name), (frame.Id, frame.Name), "real game response correlation");
                    response = MessagePackSerializer.Deserialize<T>(frame.Content);
                    if (!fenceAfter)
                        return (response, pushes);
                }
            }
            throw new InvalidDataException($"Game socket closed before {typeof(T).Name} finished.");
        }

        public void Dispose() => client.Dispose();
    }
}
