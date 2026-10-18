using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.KeyRing;

internal enum KeyRingPlatform
{
    Windows,
    MacOS,
    Other,
}

/// <summary>Why a ring has no protection: nothing is configured or available (<see cref="NoProvider"/>), the operator opted out (<see cref="Disabled"/>), or what was configured or chosen could not be used (<see cref="Unavailable"/>).</summary>
internal enum UnprotectedReason
{
    None,
    NoProvider,
    Disabled,
    Unavailable,
}

/// <summary>
/// One way of protecting the Data Protection key ring at rest, resolved once at startup from
/// <c>keyRing</c> in draughtsman.yaml and the platform (<see cref="KeyRingProtectionResolver"/>). <see cref="Apply"/>
/// is the single place it is wired into the host, and migration verifies the ring through exactly that wiring, so
/// what it checks is what will run.
/// </summary>
internal abstract class KeyRingProtection
{
    /// <summary><c>dpapi</c>, <c>keychain</c>, <c>key</c>, <c>certificate</c> or <c>none</c>: stable identifiers for the status API.</summary>
    public abstract string Kind { get; }

    /// <summary>One line naming what protects the ring, for the startup log. Names a variable, file or store; never a value.</summary>
    public abstract string Description { get; }

    /// <summary>Whether keys are actually encrypted with this (false for <see cref="NoKeyRingProtection"/>).</summary>
    public virtual bool IsActive => true;

    /// <summary>Anything worth telling the operator at startup that is not an error (a key file readable by other users).</summary>
    public IReadOnlyList<string> Notes { get; protected init; } = [];

    /// <summary>Adds the encryptor (for new keys) and whatever the framework needs to decrypt keys it reads back.</summary>
    public abstract void Apply(IDataProtectionBuilder builder);
}

/// <summary>No encryption: the ring stays as the framework writes it by default, plain text in <c>keys/</c>.</summary>
internal sealed class NoKeyRingProtection(UnprotectedReason reason, string detail) : KeyRingProtection
{
    public override string Kind => KeyRingConfig.ProtectionNone;

    public override string Description => detail;

    public override bool IsActive => false;

    public UnprotectedReason Reason { get; } = reason;

    public override void Apply(IDataProtectionBuilder builder)
    {
        // Nothing to add: the framework's default, which keeps keys unencrypted and logs its own warning per new key.
    }
}

/// <summary>Windows DPAPI, scoped to the account the server runs as, through the framework's own encryptor.</summary>
internal sealed class DpapiKeyRingProtection : KeyRingProtection
{
    /// <summary>
    /// Whether this process can use DPAPI as itself right now: has the framework's own DPAPI encryptor (the one every key the ring writes goes
    /// through) protect a small element. Null when it works, otherwise a short reason. A logon that has no DPAPI master key (an SSH public-key
    /// session, a scheduled task run without a stored password) fails here, where the same encryptor used to fail only later, as a 500 on the
    /// first sign-in. Off Windows (a test standing in for it) there is nothing to probe.
    /// </summary>
    public static string? Probe()
    {
        return OperatingSystem.IsWindows() ? ProbeWindows() : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ProbeWindows()
    {
        try
        {
            new Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlEncryptor(protectToLocalMachine: false, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                .Encrypt(new System.Xml.Linq.XElement("probe", "x"));
            return null;
        }
        catch (CryptographicException ex)
        {
            return $"{ex.GetType().Name}: {ex.Message.Trim()}";
        }
    }

    public override string Kind => KeyRingConfig.ProtectionDpapi;

    public override string Description => "Windows DPAPI (the account the server runs as)";

    public override void Apply(IDataProtectionBuilder builder)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
        }

        builder.ProtectKeysWithDpapi();
    }
}

/// <summary>The framework's certificate encryptor, for an operator who prefers a PKCS#12 certificate to an environment secret.</summary>
internal sealed class CertificateKeyRingProtection(X509Certificate2 certificate, string path) : KeyRingProtection
{
    public override string Kind => KeyRingConfig.ProtectionCertificate;

    public override string Description => $"the certificate {Path.GetFileName(path)} (thumbprint {certificate.Thumbprint})";

    public override void Apply(IDataProtectionBuilder builder)
    {
        // Both halves: Protect encrypts new keys to the certificate; Unprotect lets the framework use its private key
        // to read them back (it would otherwise go looking in a certificate store this host may not have).
        builder.ProtectKeysWithCertificate(certificate);
        builder.UnprotectKeysWithAnyCertificate(certificate);
    }
}

/// <summary>A key-encryption key held outside the data folder: an environment variable, a root-only file, or the macOS Keychain.</summary>
internal sealed class KekKeyRingProtection : KeyRingProtection
{
    private readonly byte[] _key;
    private readonly string _kind;
    private readonly string _description;

    public KekKeyRingProtection(string kind, string description, KeyEncryptionKeyLookup lookup)
    {
        _kind = kind;
        _description = description;
        _key = lookup.Key;
        Created = lookup.Created;
        Notes = lookup.Notes;
    }

    public override string Kind => _kind;

    public override string Description => _description;

    /// <summary>True when the key was minted just now (a first run, or a Keychain item that had gone missing).</summary>
    public bool Created { get; }

    public override void Apply(IDataProtectionBuilder builder)
    {
        builder.Services.AddSingleton(new KeyEncryptionKeyHolder(_key));
        builder.Services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = new KekXmlEncryptor(_key));
    }
}

/// <summary>
/// Chooses the protection for this run. Order under <c>auto</c>: a named certificate, then a key-encryption key from
/// the environment variable or key file, then the operating system store (DPAPI on Windows, the Keychain on macOS).
/// Every failure is a <see cref="NoKeyRingProtection"/> carrying a plain reason, never an exception: a server whose
/// protection cannot be set up still starts (the ring simply stays as it is) and says so, on the log and on the admin
/// AI page. The exceptions are the macOS Keychain and Windows DPAPI being unusable (<see cref="KeyRingProtectionRequiredException"/>):
/// a host that cannot reach the store it was told to protect the ring with does not start with a ring that should be protected
/// sitting as plain text, and is told the two safe choices instead.
/// </summary>
internal sealed class KeyRingProtectionResolver(
    KeyRingConfig config,
    string dataFolder,
    KeyRingPlatform platform,
    Func<string, string?> environment,
    Func<string, IKeyEncryptionKeySource>? keychain = null,
    Func<string?>? dpapiProbe = null)
{
    public static KeyRingPlatform CurrentPlatform => OperatingSystem.IsWindows() ? KeyRingPlatform.Windows : OperatingSystem.IsMacOS() ? KeyRingPlatform.MacOS : KeyRingPlatform.Other;

    public KeyRingProtection Resolve()
    {
        string requested = (config.Protection ?? KeyRingConfig.ProtectionAuto).Trim().ToLowerInvariant();
        try
        {
            return requested switch
            {
                KeyRingConfig.ProtectionNone => new NoKeyRingProtection(UnprotectedReason.Disabled, "Key ring protection is switched off (keyRing.protection: none in draughtsman.yaml, or DRAUGHTSMAN_KEY_RING_PROTECTION=none), so the ring is deliberately left as plain text."),
                KeyRingConfig.ProtectionAuto => Auto(),
                KeyRingConfig.ProtectionDpapi => Dpapi(),
                KeyRingConfig.ProtectionKeychain => Keychain(),
                KeyRingConfig.ProtectionKey => Key(required: true) ?? throw new KeyEncryptionKeyException($"keyRing.protection is key, but neither the environment variable {config.KeyEnvironmentVariable} nor keyRing.keyFile supplies a key-encryption key."),
                KeyRingConfig.ProtectionCertificate => Certificate(required: true)!,
                _ => throw new KeyEncryptionKeyException($"keyRing.protection \"{config.Protection}\" is not one of auto, dpapi, keychain, key, certificate or none."),
            };
        }
        catch (Exception ex) when (ex is KeyEncryptionKeyException or PlatformNotSupportedException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            // The message of a KeyEncryptionKeyException is written never to carry the key; for the others only the type is shown.
            string reason = ex is KeyEncryptionKeyException ? ex.Message : $"{ex.GetType().Name}.";
            return new NoKeyRingProtection(UnprotectedReason.Unavailable, reason);
        }
    }

    private KeyRingProtection Auto()
    {
        if (!string.IsNullOrWhiteSpace(config.Certificate.Path))
        {
            return Certificate(required: true)!;
        }

        KeyRingProtection? key = Key(required: false);
        if (key is not null)
        {
            return key;
        }

        return platform switch
        {
            KeyRingPlatform.Windows => Dpapi(),
            KeyRingPlatform.MacOS => Keychain(),
            _ => new NoKeyRingProtection(UnprotectedReason.NoProvider, $"No key-encryption key is configured: set the environment variable {config.KeyEnvironmentVariable} to a key held outside the data folder (openssl rand -base64 32), or set keyRing.keyFile or keyRing.certificate in draughtsman.yaml."),
        };
    }

    private KeyRingProtection Dpapi()
    {
        if (platform != KeyRingPlatform.Windows)
        {
            throw new KeyEncryptionKeyException("keyRing.protection is dpapi, which exists only on Windows.");
        }

        // Used to start, say "Windows DPAPI" and 500 on the first sign-in. A DPAPI that cannot work for this logon stops startup, like the
        // Keychain: never a quiet downgrade to a ring left as plain text, never a server that answers 500 to everyone who signs in.
        if ((dpapiProbe ?? DpapiKeyRingProtection.Probe)() is { } reason)
        {
            throw new KeyRingProtectionRequiredException(DpapiUnusable(reason, config.KeyEnvironmentVariable));
        }

        return new DpapiKeyRingProtection();
    }

    /// <summary>The startup message when Windows DPAPI cannot be used: what failed, and the two safe choices. Never a silent downgrade to an unprotected ring.</summary>
    internal static string DpapiUnusable(string reason, string keyVariable)
    {
        return $"Windows DPAPI cannot be used to protect the key ring: {reason} "
            + "This happens when the account running the server has no DPAPI master key: a logon that did not load the user's profile (an SSH public-key session, a scheduled task run without a stored password, a runas /netonly shell). "
            + "A Windows service running as LocalService or a signed-in console user has one. "
            + "Draughtsman will not start with the key ring left unprotected unless you choose that. Pick one: "
            + $"(1) give it a key-encryption key held outside the data folder: set the environment variable {keyVariable} to 32 or more random bytes, base64 encoded (docs/admin-guide.md shows how), "
            + "or set keyRing.protection: key and keyRing.keyFile in draughtsman.yaml; "
            + $"(2) run without protection as a deliberate opt-out: set keyRing.protection: none in draughtsman.yaml, or {KeyRingSetup.ProtectionOverrideVariable}=none.";
    }

    private KeyRingProtection Keychain()
    {
        if (platform != KeyRingPlatform.MacOS)
        {
            throw new KeyEncryptionKeyException("keyRing.protection is keychain, which exists only on macOS.");
        }

        string keysFolder = KeyRingFiles.KeysFolder(dataFolder);
        string account = KeyRingFiles.KeychainAccount(keysFolder);
        IKeyEncryptionKeySource source = keychain is not null ? keychain(account) : new MacKeychainKeySource(account);
        KeyEncryptionKeyLookup lookup;
        try
        {
            lookup = source.Load();
        }
        catch (KeyEncryptionKeyException ex)
        {
            throw new KeyRingProtectionRequiredException(KeychainUnusable(ex.Message, config.KeyEnvironmentVariable));
        }

        return new KekKeyRingProtection(KeyRingConfig.ProtectionKeychain, source.Description, lookup);
    }

    /// <summary>The startup message when the Keychain cannot be used: what failed, and the two safe choices. Never a silent downgrade to an unprotected ring.</summary>
    internal static string KeychainUnusable(string reason, string keyVariable)
    {
        return $"The macOS Keychain cannot be used to protect the key ring: {reason} "
            + "This happens when the account running the server has no login keychain (a service, a root LaunchDaemon, a missing or locked home folder). "
            + "Draughtsman will not start with the key ring left unprotected unless you choose that. Pick one: "
            + $"(1) give it a key-encryption key held outside the data folder: set the environment variable {keyVariable} (openssl rand -base64 32), "
            + "or set keyRing.protection: key and keyRing.keyFile in draughtsman.yaml; "
            + $"(2) run without protection as a deliberate opt-out: set keyRing.protection: none in draughtsman.yaml, or {KeyRingSetup.ProtectionOverrideVariable}=none.";
    }

    /// <summary>The environment variable wins over the file when both are set: it is the more deliberate, per-deployment act.</summary>
    private KekKeyRingProtection? Key(bool required)
    {
        IKeyEncryptionKeySource? source = null;
        if (!string.IsNullOrWhiteSpace(config.KeyEnvironmentVariable) && !string.IsNullOrWhiteSpace(environment(config.KeyEnvironmentVariable)))
        {
            source = new EnvironmentKeySource(config.KeyEnvironmentVariable, environment);
        }
        else if (!string.IsNullOrWhiteSpace(config.KeyFile))
        {
            source = new FileKeySource(config.KeyFile, dataFolder);
        }

        if (source is null)
        {
            return required ? throw new KeyEncryptionKeyException($"No key-encryption key: the environment variable {config.KeyEnvironmentVariable} is not set and keyRing.keyFile is empty.") : null;
        }

        return new KekKeyRingProtection(KeyRingConfig.ProtectionKey, source.Description, source.Load());
    }

    private CertificateKeyRingProtection? Certificate(bool required)
    {
        string path = config.Certificate.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return required ? throw new KeyEncryptionKeyException("keyRing.protection is certificate, but keyRing.certificate.path is empty.") : null;
        }

        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new KeyEncryptionKeyException($"The certificate file {path} does not exist.");
        }

        string? password = string.IsNullOrWhiteSpace(config.Certificate.PasswordEnvironmentVariable) ? null : environment(config.Certificate.PasswordEnvironmentVariable);
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(full, string.IsNullOrEmpty(password) ? null : password);
        }
        catch (CryptographicException)
        {
            throw new KeyEncryptionKeyException($"The certificate file {path} could not be opened as a PKCS#12 (.pfx) file. Check the file and the password in the environment variable {config.Certificate.PasswordEnvironmentVariable}.");
        }

        if (!certificate.HasPrivateKey || certificate.GetRSAPrivateKey() is null)
        {
            certificate.Dispose();
            throw new KeyEncryptionKeyException($"The certificate in {path} needs an RSA private key to protect the key ring.");
        }

        return new CertificateKeyRingProtection(certificate, full);
    }
}
