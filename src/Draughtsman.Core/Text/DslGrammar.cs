namespace Draughtsman.Core.Text;

/// <summary>The DSL grammar embedded from docs/spec/dsl.grammar, for anything that wants the file itself (the AI generate-from-text system prompt).</summary>
public static class DslGrammar
{
    private const string ResourceName = "schemas/dsl.grammar";

    private static readonly Lazy<string> s_rawText = new(ReadRaw);

    public static string RawText => s_rawText.Value;

    private static string ReadRaw()
    {
        using Stream stream = typeof(DslGrammar).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
