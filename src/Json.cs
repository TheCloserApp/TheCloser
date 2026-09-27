using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace TheCloser
{
    /// <summary>Thin wrapper over the JSON serializer that ships with .NET Framework.</summary>
    internal static class Json
    {
        private static JavaScriptSerializer Make()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 512;
            return s;
        }

        public static string Serialize(object value)
        {
            return Make().Serialize(value);
        }

        public static object Parse(string json)
        {
            return Make().DeserializeObject(json);
        }

        public static T Deserialize<T>(string json)
        {
            return Make().Deserialize<T>(json);
        }

        /// <summary>Walks nested objects by key; returns null when any step is missing.</summary>
        public static object Get(object node, params string[] path)
        {
            foreach (var key in path)
            {
                var dict = node as IDictionary<string, object>;
                if (dict == null || !dict.TryGetValue(key, out node)) return null;
            }
            return node;
        }

        public static string Str(object node, params string[] path)
        {
            var v = Get(node, path);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>Builds a JSON object from alternating key/value arguments.</summary>
        public static Dictionary<string, object> Obj(params object[] keyValues)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < keyValues.Length; i += 2)
                d[(string)keyValues[i]] = keyValues[i + 1];
            return d;
        }
    }
}
