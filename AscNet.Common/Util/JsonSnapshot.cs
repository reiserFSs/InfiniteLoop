using Newtonsoft.Json.Linq;

namespace AscNet.Common.Util
{
    public static class JsonSnapshot
    {
        public static JObject LoadObject(string relativePath)
        {
            string path = ResolvePath(relativePath);
            return File.Exists(path)
                ? JObject.Parse(File.ReadAllText(path))
                : new JObject();
        }

        public static int ReadInt(JObject data, string name)
        {
            return data[name]?.Value<int>() ?? 0;
        }

        public static List<dynamic> ReadDynamicList(JToken? token)
        {
            if (token is not JArray array)
                return [];

            List<dynamic> result = new();
            foreach (JToken value in array)
            {
                result.Add(ReadDynamic(value)!);
            }

            return result;
        }

        public static dynamic? ReadDynamic(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null)
                return null;

            return token.Type switch
            {
                JTokenType.Object => ReadDynamicDictionary((JObject)token),
                JTokenType.Array => ReadDynamicList(token),
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Integer => ReadInteger(token),
                JTokenType.Float => token.Value<double>(),
                JTokenType.String => token.Value<string>(),
                _ => null
            };
        }

        private static object ReadInteger(JToken token)
        {
            long value = token.Value<long>();
            return value >= int.MinValue && value <= int.MaxValue
                ? (int)value
                : value;
        }

        private static Dictionary<dynamic, dynamic> ReadDynamicDictionary(JObject data)
        {
            Dictionary<dynamic, dynamic> result = new();
            foreach (JProperty property in data.Properties())
            {
                dynamic key = int.TryParse(property.Name, out int numericKey)
                    ? numericKey
                    : property.Name;
                result[key] = ReadDynamic(property.Value);
            }

            return result;
        }

        private static string ResolvePath(string relativePath)
        {
            if (File.Exists(relativePath))
                return relativePath;

            string resourcePath = Path.Combine("Resources", relativePath);
            return resourcePath;
        }
    }
}
