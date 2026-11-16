using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Shared JSON serialization settings and custom converters for Unity types
    /// (Vector2/3/4, Quaternion, Color). Provides a pre-configured JsonSerializer instance.
    /// </summary>
    public static class JsonHelper
    {
        static JsonSerializerSettings _settings;
        static JsonSerializer _serializer;

        public static JsonSerializerSettings Settings
        {
            get
            {
                if (_settings == null)
                {
                    _settings = new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore,
                        Formatting = Formatting.None,
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                        Converters =
                        {
                            new Vector2Converter(),
                            new Vector3Converter(),
                            new Vector4Converter(),
                            new QuaternionConverter(),
                            new ColorConverter(),
                            new BoundsConverter(),
                            new RectConverter()
                        }
                    };
                }
                return _settings;
            }
        }

        public static JsonSerializer Serializer
        {
            get
            {
                if (_serializer == null)
                    _serializer = JsonSerializer.Create(Settings);
                return _serializer;
            }
        }

        public static string Serialize(object obj)
        {
            return JsonConvert.SerializeObject(obj, Settings);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        public static JObject SerializeToJObject(object obj)
        {
            return JObject.FromObject(obj, Serializer);
        }
    }

    public sealed class Vector2Converter : JsonConverter<Vector2>
    {
        public override void WriteJson(JsonWriter writer, Vector2 value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            writer.WriteValue(value.x);
            writer.WriteValue(value.y);
            writer.WriteEndArray();
        }

        public override Vector2 ReadJson(JsonReader reader, Type objectType, Vector2 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default;

            var arr = JArray.Load(reader);
            return new Vector2(
                arr.Count > 0 ? arr[0].Value<float>() : 0f,
                arr.Count > 1 ? arr[1].Value<float>() : 0f
            );
        }
    }

    public sealed class Vector3Converter : JsonConverter<Vector3>
    {
        public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            writer.WriteValue(value.x);
            writer.WriteValue(value.y);
            writer.WriteValue(value.z);
            writer.WriteEndArray();
        }

        public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default;

            var arr = JArray.Load(reader);
            return new Vector3(
                arr.Count > 0 ? arr[0].Value<float>() : 0f,
                arr.Count > 1 ? arr[1].Value<float>() : 0f,
                arr.Count > 2 ? arr[2].Value<float>() : 0f
            );
        }
    }

    public sealed class Vector4Converter : JsonConverter<Vector4>
    {
        public override void WriteJson(JsonWriter writer, Vector4 value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            writer.WriteValue(value.x);
            writer.WriteValue(value.y);
            writer.WriteValue(value.z);
            writer.WriteValue(value.w);
            writer.WriteEndArray();
        }

        public override Vector4 ReadJson(JsonReader reader, Type objectType, Vector4 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default;

            var arr = JArray.Load(reader);
            return new Vector4(
                arr.Count > 0 ? arr[0].Value<float>() : 0f,
                arr.Count > 1 ? arr[1].Value<float>() : 0f,
                arr.Count > 2 ? arr[2].Value<float>() : 0f,
                arr.Count > 3 ? arr[3].Value<float>() : 0f
            );
        }
    }

    public sealed class QuaternionConverter : JsonConverter<Quaternion>
    {
        public override void WriteJson(JsonWriter writer, Quaternion value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            writer.WriteValue(value.x);
            writer.WriteValue(value.y);
            writer.WriteValue(value.z);
            writer.WriteValue(value.w);
            writer.WriteEndArray();
        }

        public override Quaternion ReadJson(JsonReader reader, Type objectType, Quaternion existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default;

            var arr = JArray.Load(reader);
            return new Quaternion(
                arr.Count > 0 ? arr[0].Value<float>() : 0f,
                arr.Count > 1 ? arr[1].Value<float>() : 0f,
                arr.Count > 2 ? arr[2].Value<float>() : 0f,
                arr.Count > 3 ? arr[3].Value<float>() : 0f
            );
        }
    }

    public sealed class ColorConverter : JsonConverter<Color>
    {
        public override void WriteJson(JsonWriter writer, Color value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            writer.WriteValue(value.r);
            writer.WriteValue(value.g);
            writer.WriteValue(value.b);
            writer.WriteValue(value.a);
            writer.WriteEndArray();
        }

        public override Color ReadJson(JsonReader reader, Type objectType, Color existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default;

            var arr = JArray.Load(reader);
            return new Color(
                arr.Count > 0 ? arr[0].Value<float>() : 0f,
                arr.Count > 1 ? arr[1].Value<float>() : 0f,
                arr.Count > 2 ? arr[2].Value<float>() : 0f,
                arr.Count > 3 ? arr[3].Value<float>() : 1f
            );
        }
    }

    public sealed class BoundsConverter : JsonConverter<Bounds>
    {
        public override void WriteJson(JsonWriter writer, Bounds value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("center");
            writer.WriteStartArray();
            writer.WriteValue(value.center.x);
            writer.WriteValue(value.center.y);
            writer.WriteValue(value.center.z);
            writer.WriteEndArray();
            writer.WritePropertyName("size");
            writer.WriteStartArray();
            writer.WriteValue(value.size.x);
            writer.WriteValue(value.size.y);
            writer.WriteValue(value.size.z);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        public override Bounds ReadJson(JsonReader reader, Type objectType, Bounds existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default;

            var obj = JObject.Load(reader);
            var center = Vector3.zero;
            var size = Vector3.zero;

            if (obj.TryGetValue("center", out var centerToken) && centerToken is JArray ca)
            {
                center = new Vector3(
                    ca.Count > 0 ? ca[0].Value<float>() : 0f,
                    ca.Count > 1 ? ca[1].Value<float>() : 0f,
                    ca.Count > 2 ? ca[2].Value<float>() : 0f
                );
            }

            if (obj.TryGetValue("size", out var sizeToken) && sizeToken is JArray sa)
            {
                size = new Vector3(
                    sa.Count > 0 ? sa[0].Value<float>() : 0f,
                    sa.Count > 1 ? sa[1].Value<float>() : 0f,
                    sa.Count > 2 ? sa[2].Value<float>() : 0f
                );
            }

            return new Bounds(center, size);
        }
    }

    public sealed class RectConverter : JsonConverter<Rect>
    {
        public override void WriteJson(JsonWriter writer, Rect value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(value.x);
            writer.WritePropertyName("y");
            writer.WriteValue(value.y);
            writer.WritePropertyName("width");
            writer.WriteValue(value.width);
            writer.WritePropertyName("height");
            writer.WriteValue(value.height);
            writer.WriteEndObject();
        }

        public override Rect ReadJson(JsonReader reader, Type objectType, Rect existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default;

            var obj = JObject.Load(reader);
            return new Rect(
                obj.TryGetValue("x", out var x) ? x.Value<float>() : 0f,
                obj.TryGetValue("y", out var y) ? y.Value<float>() : 0f,
                obj.TryGetValue("width", out var w) ? w.Value<float>() : 0f,
                obj.TryGetValue("height", out var h) ? h.Value<float>() : 0f
            );
        }
    }
}
