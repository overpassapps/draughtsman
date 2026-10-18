using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The same canonical form scripts/upgrade-fixture/client.mjs hashes (keys sorted, <c>JSON.stringify</c> escaping and number
/// spelling), so a document hash recorded by an old build's populate run can be compared byte for byte with what this build serves.
/// </summary>
internal static class Canonical
{
    public static string Hash(JsonElement element)
    {
        return Sha256(Encoding.UTF8.GetBytes(Write(element)));
    }

    public static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string Write(JsonElement element)
    {
        var text = new StringBuilder();
        Append(text, element);
        return text.ToString();
    }

    private static void Append(StringBuilder text, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                bool first = true;
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        text.Append(',');
                    }

                    first = false;
                    AppendString(text, property.Name);
                    text.Append(':');
                    Append(text, property.Value);
                }

                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        text.Append(',');
                    }

                    firstItem = false;
                    Append(text, item);
                }

                text.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(text, element.GetString()!);
                break;
            case JsonValueKind.Number:
                text.Append(Number(element.GetDouble()));
                break;
            case JsonValueKind.True:
                text.Append("true");
                break;
            case JsonValueKind.False:
                text.Append("false");
                break;
            default:
                text.Append("null");
                break;
        }
    }

    /// <summary>How JavaScript prints a double: an integer without a point, otherwise the shortest round-trip digits.</summary>
    private static string Number(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e21)
        {
            return value == 0 ? "0" : value.ToString("0", CultureInfo.InvariantCulture);
        }

        return value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
    }

    private static void AppendString(StringBuilder text, string value)
    {
        text.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    bool loneSurrogate = char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) && !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]));
                    if (c < 0x20 || loneSurrogate)
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        text.Append('"');
    }
}
