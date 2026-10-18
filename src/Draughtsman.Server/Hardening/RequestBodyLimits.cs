using Draughtsman.Core.Media;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// How large a request body each class of route may carry (AU-07). Kestrel's own default is 30 MB for every route,
/// anonymous ones included, and a document body costs several times its size in memory while it is parsed, so the
/// limit is chosen per route class: tiny for sign-in, a few MiB for text imports, the document limit for documents and
/// MCP, the asset limit for uploads, and <see cref="LimitsConfig.MaxRequestBodyBytes"/> for everything else.
/// </summary>
public sealed class RequestBodyLimits
{
    /// <summary>Multipart framing around a file: boundaries and the part headers. Generous, not exact.</summary>
    public const long MultipartOverheadBytes = 64 * 1024;

    /// <summary>Sign-in and first-run carry an email, a name, a password and a setup code.</summary>
    public const long AuthBodyBytes = 16 * 1024;

    /// <summary>A prompt and an id. The AI routes never carry a document; the server reads it from the store.</summary>
    public const long AiBodyBytes = 256 * 1024;

    /// <summary>The importers accept 1,000,000 characters, which is at most 3 bytes each in UTF-8.</summary>
    public const long ImportBodyBytes = 3 * 1000 * 1000 + MultipartOverheadBytes;

    /// <summary>
    /// Pasted CSS or a page for the theme extractor (the extractor reads at most 2 Mi characters, at most 3 bytes each in UTF-8 but in
    /// practice one); 4 MiB covers every real stylesheet and a bundled one, and the rest of the body is never read.
    /// </summary>
    public const long ThemeExtractBodyBytes = 4 * 1024 * 1024;

    private readonly long _default;
    private readonly long _document;
    private readonly long _asset;

    public RequestBodyLimits(LimitsConfig limits, AssetConfig assets)
    {
        _default = Positive(limits.MaxRequestBodyBytes, new LimitsConfig().MaxRequestBodyBytes);
        _document = Positive(limits.MaxDocumentBytes, new LimitsConfig().MaxDocumentBytes);
        _asset = Positive(assets.MaxSizeBytes, new AssetConfig().MaxSizeBytes) + MultipartOverheadBytes;
    }

    /// <summary>The largest body Kestrel should ever read, the backstop above every route's own limit.</summary>
    public long Largest => Math.Max(Math.Max(_default, _document), Math.Max(Math.Max(_asset, ImportBodyBytes), ThemeExtractBodyBytes));

    /// <summary>The body limit for a request to <paramref name="path"/>.</summary>
    public long For(PathString path)
    {
        if (path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase))
        {
            return AuthBodyBytes;
        }

        if (path.StartsWithSegments("/api/assets", StringComparison.OrdinalIgnoreCase))
        {
            return _asset;
        }

        if (path.StartsWithSegments("/api/admin/branding", StringComparison.OrdinalIgnoreCase))
        {
            return BrandingImages.MaxSizeBytes + MultipartOverheadBytes;
        }

        if (path.StartsWithSegments("/api/admin/themes/extract", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeExtractBodyBytes;
        }

        if (path.StartsWithSegments("/api/import", StringComparison.OrdinalIgnoreCase))
        {
            return ImportBodyBytes;
        }

        if (path.StartsWithSegments("/api/ai", StringComparison.OrdinalIgnoreCase))
        {
            return AiBodyBytes;
        }

        if (path.StartsWithSegments("/api/documents", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/layout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/export", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return _document;
        }

        return _default;
    }

    /// <summary>
    /// What a person is told when a body to <paramref name="path"/> is refused as too large (QA P16): the limit in words they can
    /// act on. A file route names the file's own limit (the number a person was told on the page), not the byte count of the
    /// whole request with its multipart framing, which is what <see cref="For"/> returns and nobody could have guessed.
    /// </summary>
    public string Describe(PathString path)
    {
        if (path.StartsWithSegments("/api/admin/branding", StringComparison.OrdinalIgnoreCase))
        {
            return $"That file is too large: a branding image can be at most {FormatSize(BrandingImages.MaxSizeBytes)}.";
        }

        if (path.StartsWithSegments("/api/assets", StringComparison.OrdinalIgnoreCase))
        {
            return $"That file is too large: an image can be at most {FormatSize(_asset - MultipartOverheadBytes)}.";
        }

        return $"That request is too large: this part of Draughtsman accepts at most {FormatSize(For(path))} at a time.";
    }

    /// <summary>"512 KiB", "1.5 MiB": whole numbers where they are whole, one decimal where they are not.</summary>
    public static string FormatSize(long bytes)
    {
        const long Kib = 1024;
        const long Mib = 1024 * 1024;
        if (bytes >= Mib)
        {
            double mib = (double)bytes / Mib;
            return mib % 1 == 0 ? $"{(long)mib} MiB" : $"{mib:0.#} MiB";
        }

        double kib = Math.Max(1, (double)bytes / Kib);
        return kib % 1 == 0 ? $"{(long)kib} KiB" : $"{kib:0.#} KiB";
    }

    private static long Positive(long value, long fallback)
    {
        return value > 0 ? value : fallback;
    }
}
