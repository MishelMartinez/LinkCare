using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campana.Core;

public static class CampanaJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static byte[] SerializeToUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);
}
