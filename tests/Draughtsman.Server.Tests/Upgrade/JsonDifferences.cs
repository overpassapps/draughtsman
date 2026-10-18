using System.Text.Json;
using System.Text.RegularExpressions;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Server.Tests.Upgrade;

internal sealed record Normalisation(string Path, string Reason, string Since);

/// <summary>Where two JSON values differ, as JSON pointers, and which of those differences the committed list of intentional normalisations allows.</summary>
internal static class JsonDifferences
{
    public static IReadOnlyList<Normalisation> Allowed
    {
        get
        {
            string file = Path.Combine(UpgradeFixturePaths.Root, "normalisations.json");
            return JsonSerializer.Deserialize<List<Normalisation>>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip }) ?? [];
        }
    }

    public static bool IsAllowed(string pointer, IReadOnlyList<Normalisation> allowed)
    {
        return allowed.Any(n => new Regex("^" + Regex.Escape(n.Path).Replace("\\*", "[^/]+", StringComparison.Ordinal) + "$", RegexOptions.None, TimeSpan.FromSeconds(5)).IsMatch(pointer));
    }

    public static List<string> Between(JsonElement before, JsonElement after, string path = "")
    {
        var found = new List<string>();
        Compare(before, after, path, found);
        return found;
    }

    private static void Compare(JsonElement a, JsonElement b, string path, List<string> found)
    {
        if (found.Count > 40)
        {
            return;
        }

        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name)).Order(StringComparer.Ordinal))
            {
                bool inA = a.TryGetProperty(name, out JsonElement va);
                bool inB = b.TryGetProperty(name, out JsonElement vb);
                if (inA && inB)
                {
                    Compare(va, vb, $"{path}/{name}", found);
                }
                else
                {
                    found.Add($"{path}/{name}");
                }
            }
        }
        else if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] left = [.. a.EnumerateArray()];
            JsonElement[] right = [.. b.EnumerateArray()];
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                if (i < left.Length && i < right.Length)
                {
                    Compare(left[i], right[i], $"{path}/{i}", found);
                }
                else
                {
                    found.Add($"{path}/{i}");
                }
            }
        }
        else if (Canonical.Write(a) != Canonical.Write(b))
        {
            found.Add(path);
        }
    }
}
