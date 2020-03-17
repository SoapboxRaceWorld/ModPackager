using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace ModPackager
{
    public static class Serialization
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters =
            {
                new StringEnumConverter(new DefaultNamingStrategy(), false)
            },
            Formatting = Formatting.Indented
        });

        public static T Deserialize<T>(Stream stream) where T : class
        {
            using var sr = new StreamReader(stream);
            using var jr = new JsonTextReader(sr);
            return Serializer.Deserialize<T>(jr);
        }

        public static T Deserialize<T>(string str) where T : class
        {
            using var sr = new StringReader(str);
            using var jr = new JsonTextReader(sr);
            return Serializer.Deserialize<T>(jr);
        }

        public static string Serialize<T>(T obj)
        {
            using var tw = new StringWriter();
            Serializer.Serialize(tw, obj);
            return tw.ToString();
        }
    }
}
