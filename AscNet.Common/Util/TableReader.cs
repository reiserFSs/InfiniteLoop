using AscNet.Logging;
using System.Collections.Concurrent;
using System.Collections;
using System.Globalization;
using System.Reflection;

namespace AscNet.Common.Util
{
    #pragma warning disable CS8618, CS8602 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public abstract class TableReader<TSelf, TScheme>
    {
        public List<TScheme> All { get; set; }
        protected abstract string FilePath { get; }
        private readonly Logger c = new(typeof(TableReader<TSelf, TScheme>), nameof(TableReader<TSelf, TScheme>), LogLevel.DEBUG, LogLevel.DEBUG);
        private static TSelf _instance;
        
        public static TSelf Instance
        {
            get
            {
                _instance ??= Activator.CreateInstance<TSelf>();
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                if ((_instance as TableReader<TSelf, TScheme>).All == null)
                {
                    (_instance as TableReader<TSelf, TScheme>).Load();
                    (_instance as TableReader<TSelf, TScheme>).c.Debug($"{typeof(TSelf).Name} Excel Loaded From {(_instance as TableReader<TSelf, TScheme>).FilePath}");
                }

                return _instance;
            }
        }
        
        public abstract void Load();
    }

    public interface ITable
    {
        abstract static string File { get; }
    }

    public static class TableReaderV2
    {
        private static readonly ConcurrentDictionary<Type, object> cache = new();
        // ponytail: global cold-load gate; use per-table gates if concurrent cold loads become a throughput bottleneck.
        private static readonly object coldLoadGate = new();
        private static readonly Logger c = new(typeof(TableReaderV2), nameof(TableReaderV2), LogLevel.DEBUG, LogLevel.DEBUG);

        public static List<T> Parse<T>() where T : ITable
        {
            if (cache.TryGetValue(typeof(T), out object? cached))
                return (List<T>)cached;

            lock (coldLoadGate)
            {
                if (cache.TryGetValue(typeof(T), out cached))
                    return (List<T>)cached;

                List<T> result = new();

                try
                {
                    string path = JsonSnapshot.ResolvePath(T.File);
                    using (var reader = new StreamReader(path))
                    {
                        // Read the header line to get column names
                        string headerLine = reader.ReadLine()!;
                        string[] columnNames = headerLine.Split('\t');
                        var properties = columnNames
                            .Select((columnName, index) => (Property: typeof(T).GetProperty(columnName.Split('[').First()), Index: index))
                            .Where(column => column.Property != null)
                            .GroupBy(column => column.Property!)
                            .Select(group => (Property: group.Key, Columns: group.Select(column => column.Index).ToArray()))
                            .ToArray();

                        // Read data lines and parse them into objects
                        while (!reader.EndOfStream)
                        {
                            string dataLine = reader.ReadLine()!;
                            if (string.IsNullOrEmpty(dataLine))
                                break;

                            string[] values = dataLine.Split('\t');

                            T obj = MapToObject<T>(properties, values);
                            result.Add(obj);
                        }
                    }
                    c.Debug($"{typeof(T).Name} Loaded From {path}");

                    cache.TryAdd(typeof(T), result);
                }
                catch (Exception ex)
                {
                    c.Error($"An error occurred: {ex.Message}");
                }

                return result;
            }
        }


        static T MapToObject<T>((PropertyInfo Property, int[] Columns)[] properties, string[] values) where T : ITable
        {
            T obj = Activator.CreateInstance<T>();

            foreach ((PropertyInfo prop, int[] columns) in properties)
            {
                int i = columns[0];
                if (i < values.Length)
                {
                    if (prop.PropertyType == typeof(List<int>) || prop.PropertyType == typeof(List<double>)
                        || prop.PropertyType == typeof(List<string>))
                    {
                        int last = columns.Length - 1;
                        while (last >= 0 && (columns[last] >= values.Length || string.IsNullOrEmpty(values[columns[last]])))
                            last--;
                        IList list = (IList)Activator.CreateInstance(prop.PropertyType)!;
                        for (int column = 0; column <= last; column++)
                        {
                            string value = values[columns[column]];
                            if (list is List<int> integers)
                                integers.Add(string.IsNullOrEmpty(value) ? 0 : int.Parse(value, CultureInfo.InvariantCulture));
                            else if (list is List<double> numbers)
                                numbers.Add(string.IsNullOrEmpty(value) ? 0 : double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
                            else
                                list.Add(value);
                        }
                        prop.SetValue(obj, list);
                    }
                    else if (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(int?))
                    {
                        if (!string.IsNullOrEmpty(values[i]))
                            prop.SetValue(obj, int.Parse(values[i], CultureInfo.InvariantCulture));
                        else
                            prop.SetValue(obj, null);
                    }
                    else if (prop.PropertyType == typeof(double) || prop.PropertyType == typeof(double?))
                    {
                        if (!string.IsNullOrEmpty(values[i]))
                            prop.SetValue(obj, double.Parse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture));
                        else
                            prop.SetValue(obj, null);
                    }
                    else
                    {
                        prop.SetValue(obj, values[i]);
                    }
                }
            }

            return obj;
        }
    }
}

