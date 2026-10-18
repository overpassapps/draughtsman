using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.KeyRing;

/// <summary>The key-encryption key the running host decrypts the ring with, held for the life of the process and resolved by <see cref="KekXmlDecryptor"/> through dependency injection.</summary>
internal sealed class KeyEncryptionKeyHolder(byte[] key)
{
    public byte[] Key { get; } = key;
}

/// <summary>
/// Data Protection's <see cref="IXmlEncryptor"/> for a key-encryption key: the same role
/// <c>ProtectKeysWithDpapi</c> plays on Windows and <c>ProtectKeysWithCertificate</c> plays with a certificate, but
/// with a 256-bit key held outside the data folder (see <see cref="KeyEncryptionKey"/>). The framework hands it each
/// secret element of a new key (the <c>masterKey</c>); the encrypted form it returns is what lands in
/// <c>keys/key-*.xml</c> instead of the plain text.
/// </summary>
internal sealed class KekXmlEncryptor(byte[] kek) : IXmlEncryptor
{
    public static readonly XName SecretName = XName.Get("kekSecret", "urn:draughtsman:key-ring:v1");

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        byte[] plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        try
        {
            var element = new XElement(SecretName, new XAttribute("algorithm", "AES-256-GCM"), Convert.ToBase64String(KeyEncryptionKey.Seal(kek, plaintext)));
            return new EncryptedXmlInfo(element, typeof(KekXmlDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>Reverses <see cref="KekXmlEncryptor"/>. The framework finds this type from the <c>decryptorType</c> recorded in the key file and builds it from the host's services.</summary>
internal sealed class KekXmlDecryptor : IXmlDecryptor
{
    private readonly KeyEncryptionKeyHolder _holder;

    /// <summary>Takes the service provider, like the framework's own decryptors: the framework builds a decryptor from the type name in the key file, and a constructor asking for anything else is not one it will use.</summary>
    public KekXmlDecryptor(IServiceProvider services)
    {
        _holder = services.GetRequiredService<KeyEncryptionKeyHolder>();
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        byte[] sealedData;
        try
        {
            sealedData = Convert.FromBase64String(encryptedElement.Value.Trim());
        }
        catch (FormatException)
        {
            throw new CryptographicException("The protected key is not valid base64.");
        }

        byte[] plaintext = KeyEncryptionKey.Open(_holder.Key, sealedData);
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
