using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// One committed upgrade fixture (tests/upgrade-fixtures/README.md): the data folder an earlier build wrote, on SQLite and on
/// Postgres, with expected.json describing what it must still show after an upgrade. The fixtures are throwaway data made by
/// <c>scripts/make-upgrade-fixture.sh</c> from a real build of that commit, so these tests do not rebuild old versions.
/// </summary>
internal sealed class UpgradeFixture
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";

    private UpgradeFixture(string label, string directory, JsonElement manifest)
    {
        Label = label;
        Directory = directory;
        Manifest = manifest;
    }

    public string Label { get; }

    public string Directory { get; }

    public JsonElement Manifest { get; }

    public IReadOnlyList<string> Migrations => [.. Manifest.GetProperty("build").GetProperty("migrations").EnumerateArray().Select(e => e.GetString()!)];

    public string Commit => Manifest.GetProperty("build").GetProperty("commit").GetString()!;

    /// <summary>The content root of the build that wrote the data: the string its Data Protection payloads are bound to.</summary>
    public string ContentRoot => Manifest.GetProperty("contentRoot").GetString()!;

    public string KeyRingProtection => Manifest.GetProperty("keyRing").GetProperty("protection").GetString()!;

    public string? KeyEncryptionKey => Manifest.GetProperty("keyRing").TryGetProperty("keyBase64", out JsonElement key) ? key.GetString() : null;

    public bool HasLicence => Manifest.TryGetProperty("licence", out JsonElement licence) && licence.ValueKind == JsonValueKind.Object;

    public string? LicencePublicKey => HasLicence ? Manifest.GetProperty("licence").GetProperty("publicKey").GetString() : null;

    public bool Has(string provider)
    {
        return File.Exists(Path.Combine(Directory, provider, "expected.json"));
    }

    public static IReadOnlyList<UpgradeFixture> All
    {
        get
        {
            string root = UpgradeFixturePaths.Root;
            if (!System.IO.Directory.Exists(root))
            {
                return [];
            }

            return [.. System.IO.Directory.EnumerateDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .Order(StringComparer.Ordinal)
                .Select(d => new UpgradeFixture(Path.GetFileName(d), d, JsonDocument.Parse(File.ReadAllText(Path.Combine(d, "manifest.json"))).RootElement.Clone()))];
        }
    }

    public static UpgradeFixture Get(string label)
    {
        return All.Single(f => f.Label == label);
    }

    /// <summary>The labels that have a fixture for <paramref name="provider"/>, as xunit theory data.</summary>
    public static TheoryData<string> Labels(string provider)
    {
        var data = new TheoryData<string>();
        foreach (UpgradeFixture fixture in All.Where(f => f.Has(provider)))
        {
            data.Add(fixture.Label);
        }

        return data;
    }

    public JsonDocument Expected(string provider)
    {
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, provider, "expected.json")));
    }

    /// <summary>Unpacks the provider's data folder (what the build wrote under its data folder) into a new folder under <paramref name="parent"/> and returns it.</summary>
    public string ExtractDataFolder(string provider, string parent)
    {
        string target = Path.Combine(parent, "data");
        System.IO.Directory.CreateDirectory(target);
        using FileStream file = File.OpenRead(Path.Combine(Directory, provider, "data-folder.tar.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, target, overwriteFiles: true);
        return target;
    }

    /// <summary>The plain-SQL dump of the Postgres fixture, with psql's own meta-commands (which a driver cannot run) removed.</summary>
    public string PostgresDump()
    {
        using FileStream file = File.OpenRead(Path.Combine(Directory, Postgres, "dump.sql.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return string.Join('\n', reader.ReadToEnd().Split('\n').Where(line => !line.StartsWith('\\')));
    }
}
