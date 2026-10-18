using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// What the key ring tests share. The "old world" helpers write a ring and a stored AI key exactly as main did before
/// protection existed (a plain <c>AddDataProtection().PersistKeysToFileSystem</c> and the real
/// <see cref="AiSettingsStore"/>), and the readers decrypt through a service collection built here, independent of
/// the migration and status code under test, so a test can compare "what migration reports" with "what the framework
/// really does".
/// </summary>
internal static class KeyRingTestSupport
{
    /// <summary>The content root the checked-in fixture was written under, with the trailing separator the host's <c>ContentRootPath</c> carries; Data Protection binds a payload to exactly this string.</summary>
    public const string FixtureDiscriminator = "/opt/draughtsman-upgrade-fixture/key-ring-from-main/";

    public const string FixtureAiKey = "sk-fixture-main-secret-0123456789";

    public const string AiKey = "sk-test-secret-value-9876543210";

    public const string Discriminator = "/srv/draughtsman-tests";

    public static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-keyring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public static string FixtureFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "key-ring-from-main");

    /// <summary>Copies the checked-in main-written ring and settings into a fresh data folder.</summary>
    public static string CopyFixture()
    {
        string data = NewFolder();
        CopyDirectory(FixtureFolder, data);
        return data;
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }

    /// <summary>A data folder whose ring and stored key were written the way main writes them: no encryptor, plain keys.</summary>
    public static string OldWorld(string? discriminator = null, string? aiKey = AiKey)
    {
        string data = NewFolder();
        discriminator ??= Discriminator;
        using ServiceProvider provider = Provider(data, discriminator, protection: null, createKeys: true);
        if (aiKey is not null)
        {
            new AiSettingsStore(new DataFolder(data), provider.GetRequiredService<IDataProtectionProvider>(), NullLogger<AiSettingsStore>.Instance)
                .Save(new AiOverrides("openai", "gpt-4o-mini", null, null), aiKey);
        }

        return data;
    }

    /// <summary>A framework provider over a data folder's ring, wired like the host (or like main when <paramref name="protection"/> is null).</summary>
    public static ServiceProvider Provider(string dataFolder, string discriminator, KeyRingProtection? protection, bool createKeys = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IDataProtectionBuilder builder = services.AddDataProtection().SetApplicationName(discriminator).PersistKeysToFileSystem(new DirectoryInfo(KeyRingFiles.KeysFolder(dataFolder)));
        if (!createKeys)
        {
            builder.DisableAutomaticKeyGeneration();
        }

        protection?.Apply(builder);
        return services.BuildServiceProvider();
    }

    /// <summary>The stored AI key as the framework decrypts it with the given protection (null for a plain ring), or null when it cannot. Independent of migration and status code.</summary>
    public static string? ReadAiKey(string dataFolder, string discriminator, KeyRingProtection? protection)
    {
        using ServiceProvider provider = Provider(dataFolder, discriminator, protection);
        return new AiSettingsStore(new DataFolder(dataFolder), provider.GetRequiredService<IDataProtectionProvider>(), NullLogger<AiSettingsStore>.Instance).Load().ApiKey;
    }

    public static KekKeyRingProtection Kek(string? base64 = null)
    {
        string text = base64 ?? KeyEncryptionKey.Generate();
        return new KekKeyRingProtection(KeyRingConfig.ProtectionKey, "test key-encryption key", new KeyEncryptionKeyLookup(KeyEncryptionKey.Parse(text), false, []));
    }

    /// <summary>Every secret value (the base64 <c>masterKey</c> text) stored as plain text in a ring folder's key files.</summary>
    public static List<string> PlainMasterKeys(string folder)
    {
        return Directory.EnumerateFiles(folder, "*.xml")
            .Select(TryLoad)
            .OfType<XDocument>()
            .SelectMany(document => document.Descendants().Where(KeyRingFiles.RequiresEncryptionMarker))
            .SelectMany(secret => secret.Descendants("value").Select(value => value.Value.Trim()))
            .ToList();
    }

    private static XDocument? TryLoad(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (System.Xml.XmlException)
        {
            // A damaged file holds no parsable secret; the tests that plant one assert on it separately.
            return null;
        }
    }

    public static string AllText(string folder)
    {
        var text = new StringBuilder();
        foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            text.Append(File.ReadAllText(path));
        }

        return text.ToString();
    }

    public static Dictionary<string, byte[]> Bytes(string folder, string pattern = "*")
    {
        return Directory.EnumerateFiles(folder, pattern).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
    }

    public static Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        Dictionary<string, string> map = variables.ToDictionary(pair => pair.Name, pair => pair.Value);
        return name => map.GetValueOrDefault(name);
    }

    public static string NewKekText()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>A logger that keeps every formatted line, so a test can prove a secret never reached the log.</summary>
    public sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");
        }

        public string All => string.Join('\n', Lines);
    }

    /// <summary>Runs the startup sequence over a data folder and returns what the host would hold.</summary>
    public static (KeyRingStatus Status, ServiceCollection Services, CapturingLogger Log) Startup(
        string dataFolder,
        KeyRingConfig? config = null,
        Func<string, string?>? environment = null,
        KeyRingPlatform platform = KeyRingPlatform.Other,
        string? discriminator = null,
        Func<string, IKeyEncryptionKeySource>? keychain = null,
        Func<string?>? dpapiProbe = null)
    {
        var services = new ServiceCollection();
        var log = new CapturingLogger();
        KeyRingStatus status = KeyRingSetup.Configure(services, dataFolder, config ?? new KeyRingConfig(), discriminator ?? Discriminator, log, environment ?? (_ => null), platform, keychain, dpapiProbe);
        return (status, services, log);
    }
}
