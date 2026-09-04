using System.Runtime.CompilerServices;

namespace AscNet.Logging
{
    public static class LoggerFactory
    {
        public static ILogger? Logger { get; set; }

        private static ILogger InitializedLogger => Logger ?? throw new InvalidOperationException("The logger has not been initialized.");

        public static void Debug(string? message, [CallerMemberName] string? memberName = "") => InitializedLogger.Debug(message, memberName);

        public static void Error(string? message, Exception? ex = null, [CallerMemberName] string? memberName = "") => InitializedLogger.Error(message, ex, memberName);

        public static void Fatal(string? message, Exception? ex = null, [CallerMemberName] string? memberName = "") => InitializedLogger.Fatal(message, ex, memberName);

        public static void Info(string? message, [CallerMemberName] string? memberName = "") => InitializedLogger.Info(message, memberName);

        public static void Warn(string? message, Exception? ex = null, [CallerMemberName] string? memberName = "") => InitializedLogger.Warn(message, ex, memberName);

        public static void Dispose() => InitializedLogger.Dispose();

        public static void InitializeLogger(ILogger log) => Logger = log;
    }
}
