using Config.Net;

namespace AscNet.Common
{
    public interface IConfig
    {
        [Option(DefaultValue = VerboseLevel.Normal)]
        VerboseLevel VerboseLevel { get; set; }

        [Option]
        IGameServer GameServer { get; set; }

        [Option]
        IDatabase Database { get; set; }

        [Option]
        ILauncher Launcher { get; set; }

        [Option(DefaultValue = false)]
        bool SaveClientLogs { get; set; }

        [Option(DefaultValue = "")]
        string GuideGmPlayerIds { get; set; }

        [Option]
        IClient Client { get; set; }


        interface IGameServer
        {
            [Option(DefaultValue = nameof(AscNet))]
            string RegionName { get; set; }

            [Option(DefaultValue = "127.0.0.1")]
            string Host { get; set; }

            [Option(DefaultValue = (ushort)2335)]
            ushort Port { get; set; }
        }

        interface IDatabase
        {
            [Option(DefaultValue = "127.0.0.1")]
            string Host { get; set; }

            [Option(DefaultValue = (ushort)27017)]
            ushort Port { get; set; }

            [Option(DefaultValue = "asc_net")]
            string Name { get; set; }
        }

        interface ILauncher
        {
            [Option(DefaultValue = false)]
            bool Maintenance { get; set; }

            [Option(DefaultValue = "")]
            string Message { get; set; }

            [Option]
            string? MinimumPatchVersion { get; set; }

            [Option]
            string? MinimumLauncherVersion { get; set; }
        }

        /// <summary>
        /// Remote-config overrides for the game client, for local research only. Off by default.
        /// </summary>
        interface IClient
        {
            /// <summary>
            /// Sends remote-config <c>Debug = 1</c>: the client's debug environment, including its external
            /// Lua / table loaders (persistentDataPath/Lua, /Table), local InjectFix patch and login server input.
            /// </summary>
            [Option(DefaultValue = false)]
            bool Debug { get; set; }

            /// <summary>
            /// Sends <c>IsShowInspector = 1</c>. The client only opens its Proxima inspector when Debug is also on.
            /// </summary>
            [Option(DefaultValue = false)]
            bool ShowInspector { get; set; }
        }

    }

    public enum VerboseLevel
    {
        Silent = 0,
        Normal = 1,
        Debug = 2,
        SuperDebug = 3
    }
}
