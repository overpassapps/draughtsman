namespace Draughtsman.Server.Ai;

/// <summary>The provider names <c>ai.provider</c> and the admin AI settings accept, in one place.</summary>
internal static class AiProviders
{
    public const string Ollama = "ollama";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string AzureOpenAi = "azure-openai";

    public static readonly IReadOnlyList<string> All = [Ollama, OpenAi, Anthropic, AzureOpenAi];

    /// <summary>Trimmed, lower-cased, and "azureOpenAI" (the spelling the config comment once used) folded to "azure-openai". Blank stays blank.</summary>
    public static string Canonical(string? raw)
    {
        string provider = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return provider == "azureopenai" ? AzureOpenAi : provider;
    }

    public static bool IsSupported(string canonical)
    {
        return canonical is Ollama or OpenAi or Anthropic or AzureOpenAi;
    }

    /// <summary>Ollama runs without a key (fully local); every hosted provider needs one.</summary>
    public static bool RequiresKey(string canonical)
    {
        return canonical != Ollama;
    }

    /// <summary>Every provider's named HTTP client, so the connect-time policy is attached to all four whichever one is in use.</summary>
    public static IEnumerable<string> HttpClientNames => All.Select(HttpClientName);

    public static string HttpClientName(string canonical)
    {
        return canonical switch
        {
            Ollama => "ollama-ai-provider",
            OpenAi => "openai-ai-provider",
            Anthropic => "anthropic-ai-provider",
            _ => "azure-openai-ai-provider",
        };
    }
}
