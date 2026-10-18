using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using Draughtsman.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Draughtsman.Mcp.Tests;

public class ToolArgumentValidatorTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string"},
          "limit":{"type":"integer"},
          "layout":{"type":["boolean","null"]},
          "format":{"type":"string","enum":["svg","pdf"]}},
         "required":["title"]}
        """).RootElement;

    private static Dictionary<string, JsonElement> Args(string json)
    {
        return JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void A_valid_call_passes()
    {
        Assert.Null(ToolArgumentValidator.Validate("t", Schema, Args("""{"title":"x","limit":5,"layout":null,"format":"svg"}""")));
    }

    [Fact]
    public void A_missing_required_argument_is_named()
    {
        string? message = ToolArgumentValidator.Validate("create_document", Schema, Args("""{"limit":5}"""));
        Assert.NotNull(message);
        Assert.Contains("'title'", message, StringComparison.Ordinal);
        Assert.Contains("create_document", message, StringComparison.Ordinal);
        Assert.Contains("string", message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_arguments_at_all_is_reported_as_missing_not_ignored()
    {
        Assert.Contains("'title'", ToolArgumentValidator.Validate("t", Schema, null), StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_type_names_the_argument_and_what_was_expected_without_echoing_the_value()
    {
        string? message = ToolArgumentValidator.Validate("t", Schema, Args("""{"title":"x","limit":"secret-value"}"""));
        Assert.NotNull(message);
        Assert.Contains("'limit'", message, StringComparison.Ordinal);
        Assert.Contains("integer", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fractional_number_is_not_an_integer()
    {
        Assert.NotNull(ToolArgumentValidator.Validate("t", Schema, Args("""{"title":"x","limit":1.5}""")));
    }

    [Fact]
    public void An_unknown_enum_value_lists_the_accepted_ones()
    {
        string? message = ToolArgumentValidator.Validate("t", Schema, Args("""{"title":"x","format":"png"}"""));
        Assert.NotNull(message);
        Assert.Contains("svg, pdf", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_is_refused_for_a_non_nullable_optional_and_allowed_where_the_schema_says_null()
    {
        Assert.NotNull(ToolArgumentValidator.Validate("t", Schema, Args("""{"title":"x","limit":null}""")));
        Assert.Null(ToolArgumentValidator.Validate("t", Schema, Args("""{"title":"x","layout":null}""")));
    }

    [Fact]
    public async Task An_unexpected_exception_still_gives_the_generic_message_with_no_exception_text()
    {
        // The real SDK pipeline, in memory, with the filter registered exactly as the hosts register it.
        var toClient = new Pipe();
        var toServer = new Pipe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer()
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithTools<ExplodingTools>()
            .WithArgumentValidation();
        await using ServiceProvider provider = services.BuildServiceProvider();
        McpServer server = provider.GetRequiredService<McpServer>();
        Task run = server.RunAsync(CancellationToken.None);

        await using McpClient client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: CancellationToken.None);

        CallToolResult boom = await client.CallToolAsync("boom", new Dictionary<string, object?> { ["n"] = 1 }, cancellationToken: CancellationToken.None);
        Assert.True(boom.IsError);
        string text = Assert.IsType<TextContentBlock>(Assert.Single(boom.Content)).Text;
        Assert.Contains("boom", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/shadow", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);

        // And the filter still lets a well-formed call through to the tool, and stops a malformed one before it.
        CallToolResult ok = await client.CallToolAsync("fine", new Dictionary<string, object?> { ["n"] = 2 }, cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, ok.IsError);
        CallToolResult missing = await client.CallToolAsync("fine", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Contains("'n'", Assert.IsType<TextContentBlock>(Assert.Single(missing.Content)).Text, StringComparison.Ordinal);
        _ = run;
    }

    [McpServerToolType]
    private sealed class ExplodingTools
    {
        [McpServerTool(Name = "boom"), Description("Always throws an unexpected exception.")]
        public string Boom(int n) => throw new InvalidOperationException("leaked /etc/shadow detail");

        [McpServerTool(Name = "fine"), Description("Echoes.")]
        public string Fine(int n) => "ok" + n;
    }
}
