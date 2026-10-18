using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Draughtsman.Server.KeyRing;

internal enum KeyFileState
{
    /// <summary>A key whose secret is stored as plain text (the framework's <c>requiresEncryption</c> marker is still on it).</summary>
    Plaintext,

    /// <summary>A key whose secret is wrapped by an encryptor (DPAPI, a certificate, a key-encryption key).</summary>
    Encrypted,

    /// <summary>A file that holds no secret, such as a revocation record.</summary>
    NoSecret,

    /// <summary>Not well-formed XML. <see cref="KeyRingQuarantine"/> moves it aside; nothing repairs or deletes a ring file.</summary>
    Corrupt,
}

/// <param name="KeyId">The key's id when the file is a <c>&lt;key id="..."&gt;</c> element, which is how a key that the framework lists is matched to its file.</param>
internal sealed record KeyFileInfo(string Path, KeyFileState State, Guid? KeyId = null);

/// <summary>
/// Looks at the key ring's files on disk, without the framework: which keys are still plain text, and the small
/// identity file beside the ring. The ring's XML format (the <c>requiresEncryption</c> marker, the
/// <c>encryptedSecret</c> wrapper) is the documented key-storage format that <c>XmlKeyManager</c> itself reads and
/// writes; migration's end-to-end check, which loads the result through the real key manager, is what proves this
/// reading of it stays right across framework upgrades.
/// </summary>
internal static class KeyRingFiles
{
    public const string DirectoryName = "keys";

    /// <summary>Non-secret facts about the ring that the keys themselves do not carry (today: the macOS Keychain account id). Not <c>.xml</c>, so the framework's repository ignores it.</summary>
    public const string IdentityFileName = "ring-protection.json";

    public static readonly XNamespace DataProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";

    public static readonly XName RequiresEncryption = DataProtectionNamespace + "requiresEncryption";

    public static readonly XName EncryptedSecret = DataProtectionNamespace + "encryptedSecret";

    public static string KeysFolder(string dataFolder)
    {
        return Path.Combine(dataFolder, DirectoryName);
    }

    /// <summary>Every <c>*.xml</c> in the ring folder (what the framework's file-system repository reads), classified. An absent folder is an empty ring.</summary>
    public static IReadOnlyList<KeyFileInfo> Inspect(string keysFolder)
    {
        if (!Directory.Exists(keysFolder))
        {
            return [];
        }

        var files = new List<KeyFileInfo>();
        foreach (string path in Directory.EnumerateFiles(keysFolder, "*.xml").Order(StringComparer.Ordinal))
        {
            (KeyFileState state, Guid? keyId) = Classify(path);
            files.Add(new KeyFileInfo(path, state, keyId));
        }

        return files;
    }

    /// <summary>Whether <paramref name="element"/> is a secret the framework expects to be encrypted at rest.</summary>
    public static bool RequiresEncryptionMarker(XElement element)
    {
        return string.Equals((string?)element.Attribute(RequiresEncryption), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The account id under which this ring's key-encryption key lives in the macOS Keychain, created on first use.</summary>
    public static string KeychainAccount(string keysFolder)
    {
        string path = Path.Combine(keysFolder, IdentityFileName);
        if (File.Exists(path))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("keychainAccount", out JsonElement account) && account.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(account.GetString()))
                {
                    return account.GetString()!;
                }
            }
            catch (JsonException)
            {
                // A damaged identity file cannot be trusted to name an item; a new account is minted below and the old file is kept beside it as .bad.
                File.Move(path, path + ".bad", overwrite: true);
            }
        }

        string created = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(keysFolder);
        File.WriteAllText(path, JsonSerializer.Serialize(new { keychainAccount = created }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return created;
    }

    /// <summary>SHA-256 of a file's bytes as lower-case hex, for backup manifests.</summary>
    public static string Hash(string path)
    {
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static (KeyFileState State, Guid? KeyId) Classify(string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException)
        {
            // Only a parse failure is "corrupt"; a file that cannot be opened (locked, permissions) is a different problem and must not be moved aside.
            return (KeyFileState.Corrupt, null);
        }

        Guid? keyId = document.Root is { Name.LocalName: "key" } root && Guid.TryParse((string?)root.Attribute("id"), out Guid id) ? id : null;
        if (document.Descendants().Any(RequiresEncryptionMarker))
        {
            return (KeyFileState.Plaintext, keyId);
        }

        return (document.Descendants(EncryptedSecret).Any() ? KeyFileState.Encrypted : KeyFileState.NoSecret, keyId);
    }
}
