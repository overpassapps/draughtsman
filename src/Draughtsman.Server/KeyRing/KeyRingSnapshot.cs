using System.Security.Cryptography;
using System.Text;
using Draughtsman.Server.Ai;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.KeyRing;

/// <summary>
/// What the stored AI key looks like through one ring: whether the settings file holds one, and if it decrypts, the
/// plain text, held only long enough to compare two rings. Its <see cref="ToString"/> is redacted so a stray log line
/// or assertion message can never print it.
/// </summary>
internal sealed class AiKeyProbe(bool hasStoredKey, string? plainKey)
{
    private readonly string? _plainKey = plainKey;

    public bool HasStoredKey { get; } = hasStoredKey;

    /// <summary>True when a key is stored and this ring decrypted it.</summary>
    public bool Readable => _plainKey is not null;

    /// <summary>Whether both probes decrypted to the same key, compared in constant time.</summary>
    public bool SameKeyAs(AiKeyProbe other)
    {
        return _plainKey is not null && other._plainKey is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_plainKey), Encoding.UTF8.GetBytes(other._plainKey));
    }

    public override string ToString()
    {
        return HasStoredKey ? (Readable ? "AI key: stored, readable" : "AI key: stored, unreadable") : "AI key: none stored";
    }
}

/// <summary>
/// One reading of a ring folder through the real framework: which keys it can load and decrypt, and whether the
/// stored AI key opens. Built on a private service collection that wires the ring exactly as the host does
/// (<see cref="KeyRingProtection.Apply"/>) but with automatic key generation off, so looking at a ring can never add
/// a key to it.
/// </summary>
internal sealed class KeyRingSnapshot
{
    private KeyRingSnapshot(IReadOnlySet<Guid> readableKeys, int unreadableKeys, AiKeyProbe ai)
    {
        ReadableKeys = readableKeys;
        UnreadableKeys = unreadableKeys;
        Ai = ai;
    }

    public IReadOnlySet<Guid> ReadableKeys { get; }

    /// <summary>Keys the ring lists that could not be decrypted: a missing or wrong key-encryption key, an absent certificate, a damaged file.</summary>
    public int UnreadableKeys { get; }

    public AiKeyProbe Ai { get; }

    /// <param name="dataFolder">The data folder, whose <c>ai/settings.json</c> supplies the stored key.</param>
    /// <param name="ringFolder">The ring folder to read, which is <c>&lt;data&gt;/keys</c> or a staging copy of it.</param>
    /// <param name="contentRoot">The host's content root: the install path an earlier build bound its stored key to, tried read-only after the current application name.</param>
    public static KeyRingSnapshot Read(string dataFolder, string ringFolder, string contentRoot, KeyRingProtection protection)
    {
        return Read(dataFolder, ringFolder, LegacyDiscriminators.ForContentRoot(contentRoot), protection);
    }

    /// <param name="legacy">The install paths to try, read-only, for a stored key the current application name does not open.</param>
    public static KeyRingSnapshot Read(string dataFolder, string ringFolder, IReadOnlyList<LegacyCandidate> legacy, KeyRingProtection protection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IDataProtectionBuilder builder = services.AddDataProtection()
            .SetApplicationName(DataProtectionIdentity.ApplicationName)
            .DisableAutomaticKeyGeneration()
            .PersistKeysToFileSystem(new DirectoryInfo(ringFolder));
        protection.Apply(builder);

        using ServiceProvider provider = services.BuildServiceProvider();
        // A key the framework cannot decrypt is skipped with a log line rather than thrown, so "readable" is what it
        // returns and "unreadable" is every key file on disk that it did not.
        var readable = new HashSet<Guid>();
        foreach (IKey key in provider.GetRequiredService<IKeyManager>().GetAllKeys())
        {
            try
            {
                _ = key.Descriptor;
                readable.Add(key.KeyId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Listed but not decryptable: counted below with the keys the framework left out.
            }
        }

        int unreadable = KeyRingFiles.Inspect(ringFolder).Count(file => file.KeyId is { } id && !readable.Contains(id));

        using var legacyReader = new LegacySecretReader(ringFolder, protection, legacy);
        var store = new AiSettingsStore(new DataFolder(dataFolder), provider.GetRequiredService<IDataProtectionProvider>(), NullLogger<AiSettingsStore>.Instance, legacyReader);
        StoredAiSettings stored = store.Load();
        return new KeyRingSnapshot(readable, unreadable, new AiKeyProbe(stored.ApiKey is not null || stored.KeyUnreadable, stored.ApiKey));
    }
}
