using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Storage;

/// <summary>
/// The version of a document as stored: a fingerprint of its snapshot JSON, used as the base version a save is
/// written from so a stale save can be refused rather than silently overwriting what landed in between.
/// <para>
/// The snapshot is regenerated from the model on every save by <see cref="DocumentJson.Serialize"/>, and a save
/// always restamps <c>meta.updatedAt</c> from the server clock, so two different saves cannot produce the same tag
/// and identical bytes mean nothing was lost. That makes the snapshot itself the version counter.
/// </para>
/// <para>
/// A provider may keep the tag beside the snapshot (the <c>etag</c> column, AU-16), computed once when the document is
/// written, so that a conditional read or the check of a save is one indexed column read instead of deserialising,
/// re-serialising and hashing the whole snapshot on every poll. <see cref="IStore.GetDocumentTag"/> and
/// <see cref="IStore.GetStoredDocument"/> are how callers get the tag; <see cref="Compute"/> is what a provider
/// writes into that column, and what a provider with no column answers with.
/// </para>
/// </summary>
public static class DocumentVersionTag
{
    /// <summary>Names the algorithm so a future change to it is visible in the tag rather than silently comparing equal.</summary>
    private const string Prefix = "dv1:";

    /// <summary>Bytes of SHA-256 kept. 128 bits: a collision is not reachable by writing diagrams.</summary>
    private const int TagBytes = 16;

    /// <summary>
    /// The tag of a document. This is the only way to compute one: a tag is always the fingerprint of the model's
    /// canonical serialisation, never of whatever bytes a snapshot column happens to hold. Both halves of the guard -
    /// the tag a read hands out and the check a replace makes - come through here, so they cannot disagree about the
    /// version of the same document even if a stored snapshot predates a change to <see cref="Document"/>.
    /// </summary>
    public static string Compute(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Fingerprint(DocumentJson.Serialize(document));
    }

    /// <summary>
    /// The tag of a snapshot string a caller has just produced with <see cref="DocumentJson.Serialize(Document, bool)"/>,
    /// for the write path that already holds those bytes and must not serialise twice. Only valid for that string: a
    /// snapshot read back from a column is never passed here (see <see cref="Compute"/>).
    /// </summary>
    public static string OfFreshSnapshot(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Fingerprint(snapshot);
    }

    private static string Fingerprint(string snapshot)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(snapshot));
        var text = new StringBuilder(Prefix, Prefix.Length + (TagBytes * 2));
        for (int i = 0; i < TagBytes; i++)
        {
            text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
