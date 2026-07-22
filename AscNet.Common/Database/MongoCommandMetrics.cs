using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;
using AscNet.Logging;

namespace AscNet.Common.Database;

public static class MongoCommandMetrics
{
    private static readonly object Gate = new();
    private static Scope? Active;
    private static readonly ConcurrentDictionary<int, PendingCommand> Pending = new();
    private static readonly Logger Log = new(typeof(MongoCommandMetrics), LogLevel.DEBUG, LogLevel.DEBUG);

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("ASCNET_MONGO_METRICS") == "1";

    public static void Configure(ClusterBuilder builder)
    {
        if (!Enabled)
            return;

        builder.Subscribe<CommandStartedEvent>(OnStarted);
        builder.Subscribe<CommandSucceededEvent>(OnSucceeded);
        builder.Subscribe<CommandFailedEvent>(OnFailed);
    }

    public static IDisposable Begin(string scenario)
    {
        if (!Enabled)
            return NullScope.Instance;

        // ponytail: diagnostic mode serializes request scopes; use explicit Mongo command comments if concurrent attribution becomes necessary.
        Monitor.Enter(Gate);
        Scope scope = new(scenario, Active);
        Volatile.Write(ref Active, scope);
        return scope;
    }

    private static void OnStarted(CommandStartedEvent command)
    {
        Scope? scope = Volatile.Read(ref Active);
        if (scope is null)
            return;

        string collection = command.DatabaseNamespace.DatabaseName;
        if (command.Command.TryGetValue(command.CommandName, out BsonValue? value) && value.IsString)
            collection = value.AsString;

        Pending[command.RequestId] = new(scope, command.CommandName, collection, command.Command.ToBson().Length);
    }

    private static void OnSucceeded(CommandSucceededEvent command)
    {
        if (!Pending.TryRemove(command.RequestId, out PendingCommand? pending))
            return;

        long documents = command.Reply.TryGetValue("n", out BsonValue? value) && value.IsNumeric
            ? value.ToInt64()
            : 0;
        pending.Scope.Record(pending.Command, pending.Collection, command.Duration, pending.RequestBytes, command.Reply.ToBson().Length, documents, failed: false);
    }

    private static void OnFailed(CommandFailedEvent command)
    {
        if (Pending.TryRemove(command.RequestId, out PendingCommand? pending))
            pending.Scope.Record(pending.Command, pending.Collection, command.Duration, pending.RequestBytes, 0, 0, failed: true);
    }

    private sealed record PendingCommand(Scope Scope, string Command, string Collection, int RequestBytes);

    private sealed class Scope(string scenario, Scope? parent) : IDisposable
    {
        private readonly Dictionary<string, Measurement> measurements = [];
        private bool disposed;

        public void Record(string command, string collection, TimeSpan duration, int requestBytes, int responseBytes, long documents, bool failed)
        {
            lock (measurements)
            {
                string key = $"{collection}.{command}";
                if (!measurements.TryGetValue(key, out Measurement? measurement))
                    measurements.Add(key, measurement = new Measurement());
                measurement.Count++;
                measurement.Duration += duration;
                measurement.RequestBytes += requestBytes;
                measurement.ResponseBytes += responseBytes;
                measurement.Documents += documents;
                if (failed)
                    measurement.Failures++;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            try
            {
                lock (measurements)
                {
                    if (measurements.Count == 0)
                        return;

                    int count = measurements.Values.Sum(value => value.Count);
                    double milliseconds = measurements.Values.Sum(value => value.Duration.TotalMilliseconds);
                    int requestBytes = measurements.Values.Sum(value => value.RequestBytes);
                    int responseBytes = measurements.Values.Sum(value => value.ResponseBytes);
                    long documents = measurements.Values.Sum(value => value.Documents);
                    int failures = measurements.Values.Sum(value => value.Failures);
                    string breakdown = string.Join(", ", measurements.OrderBy(pair => pair.Key).Select(pair =>
                        $"{pair.Key}: count={pair.Value.Count} ms={pair.Value.Duration.TotalMilliseconds:F2}"));
                    Log.Info($"scenario={scenario} commands={count} ms={milliseconds:F2} requestBytes={requestBytes} responseBytes={responseBytes} documents={documents} failures={failures} [{breakdown}]");
                }
            }
            finally
            {
                Volatile.Write(ref Active, parent);
                Monitor.Exit(Gate);
            }
        }
    }

    private sealed class Measurement
    {
        public int Count;
        public TimeSpan Duration;
        public int RequestBytes;
        public int ResponseBytes;
        public long Documents;
        public int Failures;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
