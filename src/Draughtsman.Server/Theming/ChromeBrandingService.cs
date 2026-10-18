using Draughtsman.Core.Media;

namespace Draughtsman.Server.Theming;

/// <summary>
/// The live branding the theme endpoints serve (M3 branding upload): the chrome theme loaded at startup, with any
/// admin overrides stored under the data folder layered over it. The theme file is the fallback and the shipped
/// Overpass theme is the default, so a reset returns to exactly what the instance served before. Every mutation
/// writes through <see cref="BrandingStore"/> and then swaps an immutable snapshot, so a request either sees the whole
/// old branding or the whole new one, and the change is live without a restart.
/// <para>
/// Only the product name, logo, light-on-dark logo and favicon are overridable here. The colour tokens are the theme
/// file's, unless an admin has made an organisation theme the default (<see cref="OrgThemeService"/>): then that
/// theme's compiled CSS, name and mode are served instead. Logos and the product name stay branding settings either
/// way, so a theme change never loses an uploaded logo.
/// </para>
/// </summary>
internal sealed class ChromeBrandingService
{
    private readonly object _gate = new();
    private readonly LoadedChromeTheme _baseTheme;
    private readonly BrandingStore _store;
    private readonly OrgThemeService _orgThemes;
    private readonly string _baseCss;
    private EffectiveBranding _current;

    public ChromeBrandingService(LoadedChromeTheme baseTheme, BrandingStore store, OrgThemeService orgThemes)
    {
        ArgumentNullException.ThrowIfNull(baseTheme);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(orgThemes);
        _baseTheme = baseTheme;
        _store = store;
        _orgThemes = orgThemes;
        _baseCss = ChromeThemeCss.Emit(baseTheme.Theme);
        lock (_gate)
        {
            _current = Build(_store.Load());
        }
    }

    /// <summary>The compiled chrome tokens: the organisation default theme's when one is set, else the theme file's.</summary>
    public string Css => _orgThemes.DefaultCss ?? _baseCss;

    /// <summary>The theme file's own product name, so the admin page can say what a reset returns to.</summary>
    public string ThemeProductName => _baseTheme.Theme.ProductName;

    /// <summary>The branding in force right now, as an immutable snapshot a request can read without a lock.</summary>
    public EffectiveBranding Current
    {
        get
        {
            EffectiveBranding branding = Volatile.Read(ref _current);
            OrgTheme? orgDefault = _orgThemes.Default;
            return orgDefault is null ? branding : branding with { ThemeName = orgDefault.Name, Mode = orgDefault.Mode };
        }
    }

    /// <summary>Sets the product name; null or blank resets it to the theme's own.</summary>
    public void SetProductName(string? productName)
    {
        lock (_gate)
        {
            BrandingOverrides overrides = Volatile.Read(ref _current).Overrides with { ProductName = NormaliseName(productName) };
            Commit(overrides);
        }
    }

    /// <summary>Stores a validated logo, light-on-dark logo or favicon, replacing any earlier one for that slot.</summary>
    public void SetAsset(BrandingSlot slot, ValidatedBrandingImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate)
        {
            string fileName = $"{BaseName(slot)}{ExtensionFor(image.ContentType)}";
            string? previous = _current.Overrides.FileName(slot);
            _store.WriteAsset(fileName, image.Content);
            if (previous is not null && !string.Equals(previous, fileName, StringComparison.Ordinal))
            {
                _store.DeleteAsset(previous);
            }

            Commit(_current.Overrides.With(slot, fileName));
        }
    }

    /// <summary>Removes one slot's override, so it falls back to the theme file again.</summary>
    public void RemoveAsset(BrandingSlot slot)
    {
        lock (_gate)
        {
            _store.DeleteAsset(_current.Overrides.FileName(slot));
            Commit(_current.Overrides.With(slot, null));
        }
    }

    /// <summary>Removes every override, returning the instance to exactly the theme file's branding.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _store.DeleteAll();
            Commit(BrandingOverrides.Empty);
        }
    }

    /// <summary>Writes the metadata for the new overrides and swaps the snapshot. Call under <see cref="_gate"/>.</summary>
    private void Commit(BrandingOverrides overrides)
    {
        _store.Save(overrides);
        Volatile.Write(ref _current, Build(overrides));
    }

    private EffectiveBranding Build(BrandingOverrides overrides)
    {
        ChromeThemeAsset? logo = ReadAsset(overrides.Logo) ?? _baseTheme.Logo;
        bool uploadedLogo = overrides.Has(BrandingSlot.Logo) || overrides.Has(BrandingSlot.LogoLight);
        ChromeThemeAsset? compact = uploadedLogo ? null : _baseTheme.LogoCompact;
        return new EffectiveBranding(
            _baseTheme.Theme.Name,
            string.IsNullOrEmpty(overrides.ProductName) ? _baseTheme.Theme.ProductName : overrides.ProductName,
            _baseTheme.Theme.Mode,
            logo,
            ReadAsset(overrides.LogoLight) ?? _baseTheme.LogoLight ?? logo,
            compact,
            uploadedLogo ? null : _baseTheme.LogoCompactLight ?? compact,
            ReadAsset(overrides.Favicon) ?? _baseTheme.Favicon,
            overrides);
    }

    private ChromeThemeAsset? ReadAsset(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        byte[]? content = _store.ReadAsset(fileName);
        return content is null ? null : new ChromeThemeAsset(fileName, content, ContentTypeFor(fileName));
    }

    /// <summary>Trims a submitted name and drops control characters; blank becomes null (reset to the theme default).</summary>
    private static string? NormaliseName(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        string cleaned = new([.. productName.Where(c => !char.IsControl(c))]);
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > BrandingImages.MaxProductNameLength
            ? cleaned[..BrandingImages.MaxProductNameLength]
            : cleaned;
    }

    private static string BaseName(BrandingSlot slot)
    {
        return slot switch
        {
            BrandingSlot.Logo => "logo",
            BrandingSlot.LogoLight => "logo-light",
            BrandingSlot.Favicon => "favicon",
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown branding slot."),
        };
    }

    private static string ExtensionFor(string contentType)
    {
        return contentType switch
        {
            AssetTypes.Png => ".png",
            AssetTypes.Svg => ".svg",
            BrandingImages.Ico => ".ico",
            _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "Not an accepted branding content type."),
        };
    }

    private static string ContentTypeFor(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => AssetTypes.Png,
            ".svg" => AssetTypes.Svg,
            ".ico" => BrandingImages.Ico,
            _ => "application/octet-stream",
        };
    }
}
