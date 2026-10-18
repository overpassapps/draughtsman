using System.Diagnostics.CodeAnalysis;
using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;
using Microsoft.Net.Http.Headers;

namespace Draughtsman.Server.Documents;

/// <summary>
/// The base version a save is written from, carried as HTTP's own lost-update mechanism: the server hands out the
/// stored document's <see cref="DocumentVersionTag"/> as an <c>ETag</c> on every read, and a save quotes it back in
/// <c>If-Match</c>. The tag is server-issued and opaque, so no client has to reproduce the server's serialisation
/// to compute one.
/// <para>
/// Because the tag is an identifier and not a byte-exact validator, a save may quote it weakly (<c>W/"dv1:..."</c>).
/// A CDN, WAF or gateway that compresses or rewrites a response (Cloudflare, nginx with gzip on, Azure Front Door)
/// turns a strong ETag into a weak one on the way out, and the client then quotes back what it was handed; refusing
/// that would fail every save and rename behind such infrastructure while create and read kept working. The same
/// version is also sent as <see cref="VersionHeaderName"/>, which intermediaries leave alone, so a client has a
/// second place to read it from when a gateway drops or rewrites the ETag.
/// </para>
/// <para>
/// A save with no <c>If-Match</c> is refused rather than accepted. Draughtsman has two writers by design - a person
/// drafting in the browser and an agent amending the same document - and a save that names no base version cannot
/// be arbitrated at all, so allowing one would leave open exactly the hole the guard exists to close.
/// </para>
/// </summary>
public static class BaseVersionHeader
{
    /// <summary>
    /// The document version again, unquoted, on every response that carries it as an <c>ETag</c>. A proxy may rewrite or
    /// remove <c>ETag</c> but has no reason to touch a header it does not know, so the editor reads this one first.
    /// </summary>
    public const string VersionHeaderName = "X-Document-Version";

    /// <summary>The ETag of a document as it would be stored, in the quoted form the headers use.</summary>
    public static string ETagOf(Document document)
    {
        return Quote(DocumentVersionTag.Compute(document));
    }

    /// <summary>The ETag for a version tag the store already holds (<see cref="IStore.GetDocumentTag"/>), in the quoted form the headers use.</summary>
    public static string ETagOf(string versionTag)
    {
        ArgumentNullException.ThrowIfNull(versionTag);
        return Quote(versionTag);
    }

    /// <summary>Writes a stored version tag onto a response as its ETag, and as <see cref="VersionHeaderName"/>.</summary>
    public static void SetETag(HttpResponse response, string versionTag)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = ETagOf(versionTag);
        response.Headers[VersionHeaderName] = versionTag;
    }

    /// <summary>Writes the document's ETag (and <see cref="VersionHeaderName"/>) onto a response, so the caller's next save has a base version to quote.</summary>
    public static void SetETag(HttpResponse response, Document document)
    {
        ArgumentNullException.ThrowIfNull(response);
        SetETag(response, DocumentVersionTag.Compute(document));
    }

    /// <summary>
    /// Whether the request's <c>If-None-Match</c> names the document's current version, so a conditional read can be
    /// answered 304. Any listed tag matching counts, weak or strong (RFC 9110 uses weak comparison here); <c>*</c> matches.
    /// </summary>
    public static bool IsCurrent(HttpRequest request, string current)
    {
        ArgumentNullException.ThrowIfNull(request);
        IList<EntityTagHeaderValue> tags = request.GetTypedHeaders().IfNoneMatch;
        return tags.Count > 0 && tags.Any(tag => tag.Tag.Equals("*", StringComparison.Ordinal) || Unquote(tag.Tag.Value!) == current);
    }

    /// <summary>
    /// Reads the base version out of <c>If-Match</c>. Returns false with a Problem+JSON result when the header is
    /// absent (428) or is not exactly one entity tag (400): a list, <c>*</c> and anything that does not parse as a tag
    /// are refused. A weak tag is the same version as its strong form (a gateway weakens what it was sent), so
    /// <c>W/"dv1:..."</c> resolves to <c>"dv1:..."</c>; whether that version is still current is the caller's 412 to decide.
    /// </summary>
    public static bool TryResolve(HttpRequest request, [NotNullWhen(true)] out string? version, [NotNullWhen(false)] out IResult? problem)
    {
        ArgumentNullException.ThrowIfNull(request);
        version = null;
        problem = null;

        IList<EntityTagHeaderValue> tags = request.GetTypedHeaders().IfMatch;
        if (tags.Count == 0)
        {
            problem = string.IsNullOrWhiteSpace(request.Headers.IfMatch.ToString())
                ? Missing()
                : Malformed("If-Match must be one quoted entity tag, as returned in the ETag of a read.");
            return false;
        }

        if (tags.Count != 1)
        {
            problem = Malformed("If-Match requires exactly one base version; send one entity tag, not a list.");
            return false;
        }

        EntityTagHeaderValue tag = tags[0];
        if (tag.Tag.Equals("*", StringComparison.Ordinal))
        {
            problem = Malformed("If-Match: * does not name a base version; send the ETag of the document this save was made from.");
            return false;
        }

        // EntityTagHeaderValue keeps the W/ marker out of Tag, so a weak tag and its strong form give the same version.
        version = tag.Tag.Value!;
        return true;
    }

    /// <summary>Strips the quotes an entity tag is written with, leaving the stored version tag.</summary>
    public static string Unquote(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.Length >= 2 && tag[0] == '"' && tag[^1] == '"' ? tag[1..^1] : tag;
    }

    private static string Quote(string version)
    {
        return "\"" + version + "\"";
    }

    private static IResult Missing()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status428PreconditionRequired,
            title: "Base version required",
            detail: "This save must say which version of the document it was made from: send If-Match with the ETag of the read it started from. A save with no base version cannot be checked against writes that landed in between.");
    }

    private static IResult Malformed(string detail)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid If-Match header", detail: detail);
    }
}
