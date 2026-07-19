using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.GameServer.Commands;
using AscNet.Common.Database;
using AscNet.Logging;

namespace AscNet
{
    internal class Program
    {
        static void Main(string[] args)
        {
            UseResourceWorkingDirectory();

            // TODO: Add LogLevel parsing from appsettings file
            LoggerFactory.InitializeLogger(new Logger(typeof(Program), LogLevel.DEBUG, LogLevel.DEBUG));
            LoggerFactory.Logger.Info("Starting...");

            Player.EnsureLeaderboardIndexes();
            PacketFactory.LoadPacketHandlers();
            CommandFactory.LoadCommands();

            SDKServer.SDKServer.Main(args);
            new Thread(Server.Instance.Start) { IsBackground = true }.Start();

            AppDomain.CurrentDomain.ProcessExit += new EventHandler(KillProtocol);
        }

        static void UseResourceWorkingDirectory()
        {
            if (!File.Exists("Configs/version_config.json") && Directory.Exists("Resources/Configs"))
                Directory.SetCurrentDirectory("Resources");
        }

        static void KillProtocol(object? sender, EventArgs e)
        {
            LoggerFactory.Logger.Info("Shutting down...");

            foreach (var session in Server.Instance.Sessions)
            {
                session.Value.SendPush(new ShutdownNotify());
                session.Value.DisconnectProtocol();
            }
        }
    }
}
