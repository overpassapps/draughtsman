using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests;

/// <summary>What the schema made of one JSON text: whether it is a valid document, and the violation lines when it is not.</summary>
internal sealed record SchemaCheck(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Paths to the shared contract files copied next to the test binary, plus the schema compiled from the copy of the committed file.</summary>
internal static class ContractFiles
{
    public static readonly string ExamplePath = Path.Combine(AppContext.BaseDirectory, "contract", "order-service.json");

    public static readonly string SchemaPath = Path.Combine(AppContext.BaseDirectory, "contract", "document.schema.json");

    public static readonly string ExampleDslPath = Path.Combine(AppContext.BaseDirectory, "contract", "order-service.dsl");

    // Compiled from the file rather than the embedded copy DocumentSchema holds: the tests that use this stand for "what docs/schema says".
    private static readonly Lazy<SchemaValidator> s_schema = new(() => SchemaValidator.Compile(File.ReadAllText(SchemaPath)));

    public static SchemaValidator Schema => s_schema.Value;

    public static string ReadExample()
    {
        return File.ReadAllText(ExamplePath);
    }

    public static string ReadExampleDsl()
    {
        return File.ReadAllText(ExampleDslPath);
    }

    public static SchemaCheck Evaluate(string json)
    {
        return new SchemaCheck(Schema.Validate(json));
    }

    /// <summary>Flattens schema errors into one string so a failing assertion says what was wrong.</summary>
    public static string DescribeErrors(SchemaCheck results)
    {
        return string.Join(Environment.NewLine, results.Errors);
    }
}
