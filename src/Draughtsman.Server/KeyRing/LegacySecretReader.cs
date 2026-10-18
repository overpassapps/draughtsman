using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.KeyRing;

/// <summary>A secret that opened under a legacy install path, and which one.</summary>
internal sealed record LegacyReading(string Plaintext, LegacyCandidate Candidate);

/// <summary>
/// Reads, and only reads, a payload an earlier build protected under an application discriminator that was an install path.
/// It opens the ring folder through a private Data Protection stack wired exactly as the host wires it
/// (<see cref="KeyRingProtection.Apply"/>) but with no application discriminator of its own and automatic key generation off, so
/// it can never add a key to the ring, then asks for each candidate's protector in turn: <c>CreateProtector(path)</c> is what
/// <c>SetApplicationName(path)</c> was (the host wraps its provider once in the discriminator), which the upgrade fixtures
/// written by real earlier builds pin.
/// <para>The private stack is built on first use, so a data folder with nothing stored never pays for it, and is disposed with the reader.</para>
/// </summary>
/// <param name="ringFolder">The key ring folder to open.</param>
/// <param name="protection">How the ring is protected, wired as the host wires it.</param>
/// <param name="candidates">The install paths to try first.</param>
/// <param name="late">Further paths, worked out only if every one of <paramref name="candidates"/> fails (it can be slow: it reads old log files), at most once.</param>
internal sealed class LegacySecretReader(string ringFolder, KeyRingProtection protection, IReadOnlyList<LegacyCandidate> candidates, Func<IReadOnlyList<LegacyCandidate>>? late = null) : IDisposable
{
    private readonly object _gate = new();
    private IReadOnlyList<LegacyCandidate>? _late;
    private ServiceProvider? _services;
    private IDataProtectionProvider? _provider;
    private IReadOnlySet<Guid>? _readableKeys;
    private bool _failed;

    /// <summary>The install paths this reader tries, in order: the first-choice ones, then the late ones (which this works out now if it has not yet).</summary>
    public IReadOnlyList<LegacyCandidate> Candidates => [.. candidates, .. Late()];

    private IReadOnlyList<LegacyCandidate> Late()
    {
        lock (_gate)
        {
            return _late ??= late is null ? [] : [.. late().Where(l => !candidates.Any(c => string.Equals(c.Discriminator, l.Discriminator, StringComparison.Ordinal)))];
        }
    }

    /// <summary>The key ids the ring can decrypt now (empty when the ring cannot be opened at all), for telling a lost key from a path that does not match.</summary>
    public IReadOnlySet<Guid> ReadableKeys
    {
        get
        {
            Open();
            return _readableKeys ?? new HashSet<Guid>();
        }
    }

    /// <summary>The plain text and the path it was bound to, or null when no candidate opens it. Never throws for an unreadable payload.</summary>
    public LegacyReading? TryUnprotect(string purpose, string payload)
    {
        IDataProtectionProvider? provider = Open();
        return provider is null ? null : TryEach(provider, candidates, purpose, payload) ?? TryEach(provider, Late(), purpose, payload);
    }

    private static LegacyReading? TryEach(IDataProtectionProvider provider, IReadOnlyList<LegacyCandidate> paths, string purpose, string payload)
    {
        foreach (LegacyCandidate candidate in paths)
        {
            try
            {
                return new LegacyReading(provider.CreateProtector(candidate.Discriminator).CreateProtector(purpose).Unprotect(payload), candidate);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // Not this path (the payload is authenticated against it): try the next.
            }
        }

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _services?.Dispose();
            _services = null;
            _provider = null;
        }
    }

    private IDataProtectionProvider? Open()
    {
        lock (_gate)
        {
            if (_provider is not null || _failed)
            {
                return _provider;
            }

            try
            {
                var services = new ServiceCollection();
                services.AddLogging();
                IDataProtectionBuilder builder = services.AddDataProtection().DisableAutomaticKeyGeneration().PersistKeysToFileSystem(new DirectoryInfo(ringFolder));
                protection.Apply(builder);
                _services = services.BuildServiceProvider();
                var readable = new HashSet<Guid>();
                foreach (IKey key in _services.GetRequiredService<IKeyManager>().GetAllKeys())
                {
                    try
                    {
                        _ = key.Descriptor;
                        readable.Add(key.KeyId);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Listed but not decryptable (a lost key-encryption key): not readable.
                    }
                }

                _readableKeys = readable;
                _provider = _services.GetRequiredService<IDataProtectionProvider>();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A ring that cannot be opened here cannot be opened by the host either; the reader simply reads nothing.
                _failed = true;
                _services?.Dispose();
                _services = null;
            }

            return _provider;
        }
    }
}
