using Draughtsman.Core.Auth;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Auth;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// The admin AI settings (M3; docs/api.md "## AI settings"): pick the provider, endpoint and model, paste an API key,
/// and test the connection, without file or environment access. Modelled on <see cref="BrandingEndpoints"/>: every
/// route requires <see cref="AuthPolicies.CookieAdminOnly"/> (a cookie-authenticated admin, never an API token
/// whatever its owner's role - an agent's token must not be able to point the instance at a different provider or
/// replace its key), writes carry the CSRF header, and each change is logged with the acting admin's user id, since
/// the audit table is document-scoped.
/// <para>
/// The API key is write-only. It arrives in <c>PUT /key</c>, is stored encrypted, and no response, log line or error
/// body ever carries it or any part of it: the settings response says only whether one is set and where the key in
/// effect comes from.
/// </para>
/// <para>
/// Where the endpoint may point is policy (docs/ai-endpoint-policy.md, <see cref="AiEndpointPolicy"/>): a refused endpoint is a 400 that
/// changes nothing and is recorded as an <c>ai.endpoint_refused</c> security event; a change of host is recorded as
/// <c>ai.endpoint_host_changed</c> with the acting admin, and forgets the stored key.
/// </para>
/// </summary>
public static class AiSettingsEndpoints
{
    private const int MaxTextLength = 2048;
    private const int MaxModelLength = 200;
    private const int MaxApiVersionLength = 40;
    private const int MaxKeyLength = 512;

    public static IEndpointRouteBuilder MapAiSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin/ai").RequireAuthorization(AuthPolicies.CookieAdminOnly);
        group.MapGet("", Get).WithName("GetAiSettings");
        group.MapPut("", SetSettings).WithName("SetAiSettings");
        group.MapPut("/key", SetKey).WithName("SetAiApiKey");
        group.MapDelete("/key", ClearKey).WithName("ClearAiApiKey");
        group.MapPost("/reset", Reset).WithName("ResetAiSettings");
        group.MapPost("/test", Test).WithName("TestAiConnection");
        return app;
    }

    private static IResult Get(AiSettingsService settings)
    {
        return Results.Ok(settings.Describe());
    }

    private static async Task<IResult> SetSettings(SetAiSettingsRequest? body, HttpContext context, AiSettingsService settings, SecurityEventLog events, ILoggerFactory loggerFactory)
    {
        if (body is null)
        {
            return Problem("Malformed body", "The body must be a JSON object with provider, model, endpoint and apiVersion (each may be blank).");
        }

        string? provider = Blank(body.Provider);
        if (provider is not null)
        {
            provider = AiProviders.Canonical(provider);
            if (!AiProviders.IsSupported(provider))
            {
                return Problem("Unknown provider", "The provider must be one of ollama, openai, anthropic or azure-openai.");
            }
        }

        string? endpoint = Blank(body.Endpoint);
        if (endpoint is not null && EndpointProblem(endpoint) is { } endpointProblem)
        {
            // The value is never echoed: a mistyped URL can still carry a secret.
            return Problem("Invalid endpoint", endpointProblem);
        }

        string? model = Blank(body.Model);
        if (model is not null && (model.Length > MaxModelLength || model.Any(char.IsControl)))
        {
            return Problem("Invalid model", $"The model name must be a single line of at most {MaxModelLength} characters.");
        }

        string? apiVersion = Blank(body.ApiVersion);
        if (apiVersion is not null && (apiVersion.Length > MaxApiVersionLength || !apiVersion.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            return Problem("Invalid API version", "The API version may contain only letters, digits, dots, dashes and underscores, such as 2024-10-21.");
        }

        var overrides = new AiOverrides(provider, model, endpoint, apiVersion);
        if (settings.PolicyRefusalFor(overrides) is { } refusal)
        {
            await events.Record(context, SecurityEventTypes.AiEndpointRefused, detail: $"provider {refusal.Provider}, host {refusal.Authority}: {refusal.Reason}");
            return Problem("Endpoint not allowed", refusal.Reason);
        }

        AiEndpointChange change = settings.SetOverrides(overrides);
        Log(loggerFactory, context, "provider, endpoint and model");
        await RecordHostChange(events, context, change);
        return Results.Ok(settings.Describe());
    }

    private static IResult SetKey(SetAiKeyRequest? body, HttpContext context, AiSettingsService settings, ILoggerFactory loggerFactory)
    {
        string? key = body?.ApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return Problem("No key", "The body must be a JSON object with a non-empty apiKey. To remove the stored key, use DELETE.");
        }

        // Nothing of the value is echoed. A key never legitimately holds whitespace or control characters, and it goes
        // into an HTTP header, so anything like that is a paste mistake (or an injection attempt) either way.
        if (key.Length > MaxKeyLength || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return Problem("Invalid key", $"An API key is a single token of at most {MaxKeyLength} characters with no spaces or line breaks.");
        }

        if (settings.KeyRefusalFor() is { } refusal)
        {
            return Problem("Key not accepted", refusal);
        }

        settings.SetApiKey(key);
        Log(loggerFactory, context, "api key set");
        return Results.Ok(settings.Describe());
    }

    private static IResult ClearKey(HttpContext context, AiSettingsService settings, ILoggerFactory loggerFactory)
    {
        settings.ClearApiKey();
        Log(loggerFactory, context, "api key cleared");
        return Results.Ok(settings.Describe());
    }

    private static async Task<IResult> Reset(HttpContext context, AiSettingsService settings, SecurityEventLog events, ILoggerFactory loggerFactory)
    {
        AiEndpointChange change = settings.Reset();
        Log(loggerFactory, context, "reset to draughtsman.yaml");
        await RecordHostChange(events, context, change);
        return Results.Ok(settings.Describe());
    }

    private static async Task<IResult> Test(HttpContext context, AiSettingsService settings, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        AiConnectionTestResult result = await settings.TestConnectionAsync(cancellationToken);
        Log(loggerFactory, context, result.Ok ? "connection test passed" : "connection test failed");
        return Results.Ok(result);
    }

    /// <summary>Why <paramref name="endpoint"/> cannot be used, or null. http and https only, and no credentials or query in the URL (they would end up in logs and error text).</summary>
    private static string? EndpointProblem(string endpoint)
    {
        if (endpoint.Length > MaxTextLength || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return "The endpoint must be a full URL such as https://api.openai.com/v1 or http://localhost:11434.";
        }

        if (AiSettingsResolver.EndpointProblem(uri) is { } problem)
        {
            return problem;
        }

        return uri.Query.Length > 0 ? "The endpoint must not contain a query string; put the API key in the key field." : null;
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Records a move of the endpoint's host with the acting admin (the security event log's own actor); a change that left the host alone writes nothing.</summary>
    private static Task RecordHostChange(SecurityEventLog events, HttpContext context, AiEndpointChange change)
    {
        if (!change.Moved)
        {
            return Task.CompletedTask;
        }

        string key = change.KeyCleared ? "stored key cleared" : "no stored key was affected";
        return events.Record(context, SecurityEventTypes.AiEndpointHostChanged, detail: $"from {(change.From.Length == 0 ? "none" : change.From)} to {(change.To.Length == 0 ? "none" : change.To)}; {key}");
    }

    private static void Log(ILoggerFactory loggerFactory, HttpContext context, string what)
    {
        // Only what changed and who changed it: never a value, and above all never the key.
        loggerFactory.CreateLogger("Draughtsman.Server.Ai.Settings")
            .LogInformation("AI settings changed ({What}) by user {ActorUserId}.", what, context.User.UserId() ?? "unknown");
    }

    private static IResult Problem(string title, string detail)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);
    }

    /// <summary>Body of <c>PUT /api/admin/ai</c>. Each field left blank or absent clears that override, so it falls back to draughtsman.yaml.</summary>
    public sealed record SetAiSettingsRequest(string? Provider = null, string? Model = null, string? Endpoint = null, string? ApiVersion = null);

    /// <summary>Body of <c>PUT /api/admin/ai/key</c>.</summary>
    public sealed record SetAiKeyRequest(string? ApiKey = null);
}
