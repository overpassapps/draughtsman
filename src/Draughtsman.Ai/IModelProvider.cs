namespace Draughtsman.Ai;

/// <summary>
/// One chat/complete call to a configured AI model (build-spec-v0.1.md section 7: Anthropic, OpenAI, Azure OpenAI
/// or Ollama, chosen and keyed in <c>draughtsman.yaml</c>'s <c>ai</c> section). Deliberately small: a system prompt
/// plus a message list in, one completion out, so every provider slots in behind it without changing a caller.
/// Streaming is not part of this interface yet - the MVP caller (generate-from-text) reads a whole completion
/// before it parses DSL out of it, so there is nothing today that would consume partial tokens; a provider that
/// wants to stream can still do so internally and only return the assembled text.
/// </summary>
public interface IModelProvider
{
    /// <summary>The provider name as it appears in <c>ai.provider</c> and in <c>aiProvenance</c> (e.g. "ollama").</summary>
    string Provider { get; }

    /// <summary>The configured model name (e.g. "llama3.1"), recorded in <c>aiProvenance.model</c>.</summary>
    string Model { get; }

    /// <summary>
    /// Sends the system prompt and conversation to the model and returns its reply text. Throws
    /// <see cref="ModelProviderException"/> when the model cannot be reached or returns something this provider
    /// cannot turn into a completion; never returns null or partial output silently.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default);
}

/// <summary>One turn of the conversation sent to the model.</summary>
public sealed record ModelMessage(ModelRole Role, string Content);

public enum ModelRole
{
    User,
    Assistant,
}
