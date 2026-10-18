namespace Draughtsman.Server.Theming;

/// <summary>Why an organisation theme operation was refused, so an endpoint can pick the HTTP status.</summary>
internal enum OrgThemeFailure
{
    NotFound,
    Invalid,
    Conflict,
}

internal sealed class OrgThemeException(OrgThemeFailure failure, string message, IReadOnlyList<OrgThemeProblem>? problems = null) : Exception(message)
{
    public OrgThemeFailure Failure { get; } = failure;

    public IReadOnlyList<OrgThemeProblem> Problems { get; } = problems ?? [];
}

/// <summary>
/// The organisation's themes and which one is the default. Every mutation validates, writes through
/// <see cref="OrgThemeStore"/> and then swaps an immutable snapshot, so a request sees the whole old set or the whole
/// new one and a change is live without a restart (the same shape as <see cref="ChromeBrandingService"/>). The default
/// theme's chrome CSS is compiled once per change, not per request.
/// </summary>
internal sealed class OrgThemeService
{
    public const int MaxThemes = 50;

    private readonly object _gate = new();
    private readonly OrgThemeStore _store;
    private readonly ILogger<OrgThemeService> _logger;
    private readonly Lazy<OrgTheme> _shipped;
    private State _state;

    public OrgThemeService(OrgThemeStore store, ILogger<OrgThemeService> logger)
    {
        _store = store;
        _logger = logger;
        _shipped = new Lazy<OrgTheme>(LoadShipped);
        lock (_gate)
        {
            _state = Build(_store.LoadAll(), _store.LoadDefaultId());
        }
    }

    /// <summary>The organisation's themes in name order.</summary>
    public IReadOnlyList<OrgTheme> List => Volatile.Read(ref _state).Themes;

    /// <summary>The id of the default theme, or null when the instance still serves the shipped chrome theme.</summary>
    public string? DefaultId => Volatile.Read(ref _state).DefaultTheme?.Id;

    public OrgTheme? Default => Volatile.Read(ref _state).DefaultTheme;

    /// <summary>The compiled chrome CSS for the default theme, or null when there is none.</summary>
    public string? DefaultCss => Volatile.Read(ref _state).DefaultCss;

    public OrgTheme? Find(string id)
    {
        return List.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.Ordinal));
    }

    public OrgTheme Get(string id)
    {
        return Find(id) ?? throw new OrgThemeException(OrgThemeFailure.NotFound, $"There is no theme '{id}'.");
    }

    /// <summary>Creates a theme from a base: the shipped Overpass default, or a copy of an existing theme by id.</summary>
    public OrgTheme Create(string name, string? baseId)
    {
        lock (_gate)
        {
            OrgTheme source = string.IsNullOrEmpty(baseId) || baseId == "overpass" ? _shipped.Value : Get(baseId);
            return Add(source with { Name = name });
        }
    }

    public OrgTheme Duplicate(string id)
    {
        lock (_gate)
        {
            OrgTheme source = Get(id);
            return Add(source with { Name = UniqueName(source.Name + " copy") });
        }
    }

    /// <summary>Adds a theme read from a file. A name already in use gets a numeric suffix rather than refusing the file.</summary>
    public OrgTheme Import(string yaml)
    {
        OrgTheme read;
        try
        {
            read = OrgThemeYaml.Read(yaml);
        }
        catch (FormatException ex)
        {
            throw new OrgThemeException(OrgThemeFailure.Invalid, ex.Message, [new OrgThemeProblem("file", ex.Message)]);
        }

        lock (_gate)
        {
            // Validate before renaming so a missing name reads as the error it is, not as "theme 2".
            (OrgTheme checkedTheme, List<OrgThemeProblem> problems) = OrgThemeValidator.Validate(read);
            if (problems.Count > 0)
            {
                throw Invalid(problems);
            }

            return Add(checkedTheme with { Name = UniqueName(checkedTheme.Name) });
        }
    }

    public OrgTheme Update(string id, OrgTheme incoming)
    {
        lock (_gate)
        {
            OrgTheme existing = Get(id);
            (OrgTheme theme, List<OrgThemeProblem> problems) = OrgThemeValidator.Validate(incoming with { Id = id });
            if (problems.Count > 0)
            {
                throw Invalid(problems);
            }

            if (NameTaken(theme.Name, exceptId: id))
            {
                throw new OrgThemeException(OrgThemeFailure.Conflict, $"Another theme is already called '{theme.Name}'.");
            }

            OrgTheme saved = theme with { Id = existing.Id, UpdatedAt = DateTimeOffset.UtcNow };
            _store.Save(saved);
            Commit([.. List.Select(t => t.Id == id ? saved : t)], DefaultId);
            return saved;
        }
    }

    /// <summary>Removes a theme. The default cannot be deleted: make another the default (or clear it) first.</summary>
    public void Delete(string id)
    {
        lock (_gate)
        {
            Get(id);
            if (string.Equals(DefaultId, id, StringComparison.Ordinal))
            {
                throw new OrgThemeException(OrgThemeFailure.Conflict, "This is the organisation default. Make another theme the default, or go back to the Overpass default, before deleting it.");
            }

            _store.Delete(id);
            Commit([.. List.Where(t => t.Id != id)], DefaultId);
        }
    }

    /// <summary>Makes <paramref name="id"/> the theme every user receives; null returns to the shipped chrome theme.</summary>
    public void SetDefault(string? id)
    {
        lock (_gate)
        {
            if (id is not null)
            {
                Get(id);
            }

            _store.SaveDefaultId(id);
            Commit(List, id);
        }
    }

    public string Export(string id, string productName)
    {
        return OrgThemeYaml.Write(Get(id), productName);
    }

    private OrgTheme Add(OrgTheme source)
    {
        if (List.Count >= MaxThemes)
        {
            throw new OrgThemeException(OrgThemeFailure.Conflict, $"An organisation can have at most {MaxThemes} themes. Delete one first.");
        }

        string name = UniqueName(source.Name);
        string id = UniqueId(OrgThemeValidator.Slug(name));
        (OrgTheme theme, List<OrgThemeProblem> problems) = OrgThemeValidator.Validate(source with { Id = id, Name = name });
        if (problems.Count > 0)
        {
            throw Invalid(problems);
        }

        OrgTheme saved = theme with { UpdatedAt = DateTimeOffset.UtcNow };
        _store.Save(saved);
        Commit([.. List, saved], DefaultId);
        return saved;
    }

    private static OrgThemeException Invalid(List<OrgThemeProblem> problems)
    {
        return new OrgThemeException(OrgThemeFailure.Invalid, problems[0].Message, problems);
    }

    private bool NameTaken(string name, string? exceptId)
    {
        return List.Any(t => !string.Equals(t.Id, exceptId, StringComparison.Ordinal) && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private string UniqueName(string name)
    {
        string trimmed = name.Trim();
        if (!NameTaken(trimmed, null))
        {
            return trimmed;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{trimmed} {n}";
            if (!NameTaken(candidate, null))
            {
                return candidate;
            }
        }
    }

    private string UniqueId(string slug)
    {
        if (Find(slug) is null)
        {
            return slug;
        }

        for (int n = 2; ; n++)
        {
            string suffix = "-" + n;
            string candidate = (slug.Length + suffix.Length > 40 ? slug[..(40 - suffix.Length)] : slug) + suffix;
            if (Find(candidate) is null)
            {
                return candidate;
            }
        }
    }

    private void Commit(IReadOnlyList<OrgTheme> themes, string? defaultId)
    {
        Volatile.Write(ref _state, Build(themes, defaultId));
    }

    private State Build(IReadOnlyList<OrgTheme> themes, string? defaultId)
    {
        List<OrgTheme> ordered = [.. themes.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
        OrgTheme? defaultTheme = defaultId is null ? null : ordered.FirstOrDefault(t => t.Id == defaultId);
        if (defaultId is not null && defaultTheme is null)
        {
            _logger.LogWarning("The organisation default theme '{DefaultId}' no longer exists; serving the shipped theme.", defaultId);
        }

        string? css = defaultTheme is null ? null : ChromeThemeCss.Emit(defaultTheme.ToChromeTheme(OrgThemeStore.FilePlaceholderProductName));
        return new State(ordered, defaultTheme, css);
    }

    private static OrgTheme LoadShipped()
    {
        string yaml = ChromeThemeLoader.ShippedDefaultYaml()
            ?? throw new InvalidOperationException("The shipped chrome theme (themes/chrome.yaml) is not embedded in the server assembly.");
        OrgTheme read = OrgThemeYaml.Read(yaml) with { Name = "Overpass default" };
        (OrgTheme theme, List<OrgThemeProblem> problems) = OrgThemeValidator.Validate(read);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The shipped chrome theme is not a valid organisation theme: " + problems[0].Message);
        }

        return theme;
    }

    private sealed record State(IReadOnlyList<OrgTheme> Themes, OrgTheme? DefaultTheme, string? DefaultCss);
}
