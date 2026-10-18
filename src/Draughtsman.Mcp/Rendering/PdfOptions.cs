namespace Draughtsman.Mcp.Rendering;

/// <summary>The page a PDF export is printed on: a fixed paper size, or a page cut to the drawing.</summary>
public enum PdfPageSize
{
    /// <summary>The page is exactly as large as the drawing (plus margin), so nothing is scaled.</summary>
    Fit,
    A4,
    A3,
    Letter,
}

public enum PdfOrientation
{
    Landscape,
    Portrait,
}

/// <summary>
/// What the caller may choose about a PDF export (docs/api.md <c>GET /api/documents/{id}/pdf</c>): the page, its orientation and
/// the margin in millimetres. <see cref="PdfPageSize.Fit"/> ignores orientation, since the page takes the drawing's own shape.
/// A fixed page scales the drawing down (never up) to sit inside the margins, centred, so the export stays a single page.
/// </summary>
/// <param name="PageSize">The paper size; <see cref="PdfPageSize.Fit"/> by default, so an export with no options is one page.</param>
/// <param name="Orientation">Landscape by default; unused for <see cref="PdfPageSize.Fit"/>.</param>
/// <param name="MarginMm">Margin on every side; null means the default for the page size (none for Fit, 10 mm otherwise).</param>
public sealed record PdfOptions(PdfPageSize PageSize = PdfPageSize.Fit, PdfOrientation Orientation = PdfOrientation.Landscape, double? MarginMm = null)
{
    /// <summary>The largest margin accepted: half of an A4 short side would leave no page, so 50 mm is the ceiling.</summary>
    public const double MaxMarginMm = 50;

    public const double DefaultFixedMarginMm = 10;

    /// <summary>The margin actually used.</summary>
    public double EffectiveMarginMm => MarginMm ?? (PageSize == PdfPageSize.Fit ? 0 : DefaultFixedMarginMm);

    /// <summary>Null when the options are usable, else what is wrong with them.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(PageSize) || !Enum.IsDefined(Orientation))
        {
            return "Unknown page size or orientation.";
        }

        if (MarginMm is { } margin && (double.IsNaN(margin) || margin < 0 || margin > MaxMarginMm))
        {
            return $"The margin must be between 0 and {MaxMarginMm} mm.";
        }

        return null;
    }
}

/// <summary>Whether PDF export can actually run here, as proven by a browser launch: see <see cref="IPdfRenderer.ProbeAsync"/>.</summary>
/// <param name="Available">True only when a Chromium launch has succeeded recently.</param>
/// <param name="Hint">Why it is unavailable and how to fix it; null when available.</param>
/// <param name="Problem">Which part is the matter while unavailable (<see cref="PdfProblems"/>), so an admin is sent to the right fix: a missing driver is not cured by installing a browser. Null when available.</param>
public sealed record PdfStatus(bool Available, string? Hint, string? Problem = null)
{
    public static PdfStatus Ready { get; } = new(true, null);
}

/// <summary>The values of <see cref="PdfStatus.Problem"/>, also the <c>pdfProblem</c> member of <c>GET /api/health</c>.</summary>
public static class PdfProblems
{
    /// <summary>The component is switched off in <c>draughtsman.yaml</c>.</summary>
    public const string Disabled = "disabled";

    /// <summary>Playwright's driver (its Node runtime and package, the <c>.playwright</c> folder beside the executable) is missing, incomplete or cannot run. Not a browser problem.</summary>
    public const string Driver = "driver";

    /// <summary>The driver is fine but no Chromium or Chrome could be launched (not installed, wrong revision, bad <c>export.chromium.path</c>).</summary>
    public const string Browser = "browser";
}
