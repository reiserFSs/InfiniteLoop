using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.GameServer.Commands;
using AscNet.Common.Database;
using AscNet.Logging;
using System.Text.Json;

namespace AscNet
{
    internal class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--shutdown-local-mongo")
                return ShutdownLocalMongo(args);

            UseResourceWorkingDirectory();

            if (args is ["--export-gm-item-catalog", string catalogPath])
            {
                var catalog = GmItemCatalog.Current;
                using FileStream output = File.Create(catalogPath);
                JsonSerializer.Serialize(output, new { Catalog = catalog.Info(), Items = catalog.Export() },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
                return 0;
            }

            // TODO: Add LogLevel parsing from appsettings file
            LoggerFactory.InitializeLogger(new Logger(typeof(Program), LogLevel.DEBUG, LogLevel.DEBUG));
            LoggerFactory.Info("Starting...", memberName: "");

            EnsureIndexesReady();
            PacketFactory.LoadPacketHandlers();
            CommandFactory.LoadCommands();

            SDKServer.SDKServer.Main(args);
            new Thread(Server.Instance.Start) { IsBackground = true }.Start();

            AppDomain.CurrentDomain.ProcessExit += new EventHandler(KillProtocol);

            ManagedControl.Start();

            return 0;
        }

        static void EnsureIndexesReady()
        {
            const int attempts = 5;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Player.EnsureIndexes();
                    return;
                }
                catch (Exception ex) when (attempt < attempts && IsTransientMongo(ex))
                {
                    LoggerFactory.Warn($"MongoDB is not ready for index setup (attempt {attempt} of {attempts}): {ex.Message}");
                    Thread.Sleep(TimeSpan.FromMilliseconds(400 * attempt));
                }
            }
        }

        static bool IsTransientMongo(Exception ex)
        {
            for (Exception? current = ex; current != null; current = current.InnerException)
                if (current is MongoConnectionException or TimeoutException or IOException or System.Net.Sockets.SocketException)
                    return true;
            return false;
        }

        static int ShutdownLocalMongo(string[] args)
        {
            if (args.Length != 2 ||
                args[1].Length == 0 ||
                args[1].Any(c => c < '0' || c > '9') ||
                !int.TryParse(args[1], out int port) ||
                port is < 1 or > 65535)
            {
                Console.Error.WriteLine("Usage: AscNet --shutdown-local-mongo <port (1-65535)>");
                return 2;
            }
            bool shutdownSent = false;

            try
            {
                var timeout = TimeSpan.FromSeconds(2);
                var client = new MongoClient(new MongoClientSettings
                {
                    Server = new MongoServerAddress("127.0.0.1", port),
                    DirectConnection = true,
                    ConnectTimeout = timeout,
                    ServerSelectionTimeout = timeout,
                    SocketTimeout = timeout,
                    ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(
                        e => shutdownSent |= e.CommandName == "shutdown"),
                });
                client.GetDatabase("admin").RunCommand<BsonDocument>(
                    new BsonDocument("shutdown", 1));
                return 0;
            }
            catch (MongoConnectionException) when (shutdownSent)
            {
                // mongod normally closes the connection before replying to shutdown.
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to shut down local MongoDB: {ex.Message}");
                return 1;
            }
        }

        static void UseResourceWorkingDirectory()
        {
            if (!File.Exists("Configs/version_config.json") && Directory.Exists("Resources/Configs"))
                Directory.SetCurrentDirectory("Resources");
        }

        static void KillProtocol(object? sender, EventArgs e)
        {
            LoggerFactory.Info("Shutting down...", memberName: "");

            foreach (var session in Server.Instance.Sessions)
            {
                session.Value.SendPush(new ShutdownNotify());
                session.Value.DisconnectProtocol();
            }
        }
    }
}
