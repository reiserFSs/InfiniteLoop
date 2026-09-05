using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.GameServer.Commands;
using AscNet.Common.Database;
using AscNet.Logging;

namespace AscNet
{
    internal class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--shutdown-local-mongo")
                return ShutdownLocalMongo(args);

            UseResourceWorkingDirectory();

            // TODO: Add LogLevel parsing from appsettings file
            LoggerFactory.InitializeLogger(new Logger(typeof(Program), LogLevel.DEBUG, LogLevel.DEBUG));
            LoggerFactory.Info("Starting...", memberName: "");

            Player.EnsureIndexes();
            PacketFactory.LoadPacketHandlers();
            CommandFactory.LoadCommands();

            SDKServer.SDKServer.Main(args);
            new Thread(Server.Instance.Start) { IsBackground = true }.Start();

            AppDomain.CurrentDomain.ProcessExit += new EventHandler(KillProtocol);

            if (Environment.GetEnvironmentVariable("ASCNET_MANAGED_STDIN") == "1")
                new Thread(() =>
                {
                    Console.In.ReadLine();
                    Environment.Exit(0);
                }) { IsBackground = true }.Start();

            return 0;
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
