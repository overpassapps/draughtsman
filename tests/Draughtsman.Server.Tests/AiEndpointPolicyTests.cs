using System.Net;
using Draughtsman.Server.Ai;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The AI endpoint policy's rules (AU-11 option A, docs/ai-endpoint-policy.md) as pure functions, every one asserted in both directions:
/// the address that must be refused and its nearest neighbour that must not be, the host that is listed and the one that merely looks
/// like it. A rule loosened or dropped fails here before it can ship; the same rules at the request level are in
/// <see cref="AiEndpointPolicyEndpointTests"/> and <see cref="AiConnectGuardTests"/>.
/// </summary>
public class AiEndpointPolicyTests
{
    private static readonly string[] s_none = [];

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://169.254.169.254:80/")]
    [InlineData("https://169.254.169.254/v1")]
    [InlineData("http://[::ffff:169.254.169.254]/")] // bracketed, mapped
    [InlineData("http://[::ffff:a9fe:a9fe]/")]
    [InlineData("http://[fd00:ec2::254]/")]
    [InlineData("http://[fe80::1]:11434")]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/")]
    [InlineData("http://[2002:a9fe:a9fe::]/")]
    [InlineData("http://2852039166/")] // 169.254.169.254 as one decimal number
    [InlineData("http://0xA9FEA9FE/")] // as one hex number
    [InlineData("http://0251.0376.0251.0376/")] // in octal
    [InlineData("http://169.254.43518/")] // part dotted, part not
    [InlineData("http://0/")] // 0.0.0.0
    [InlineData("http://0.0.0.0:11434")]
    public void A_metadata_or_link_local_endpoint_is_refused_for_every_provider_even_when_listed(string endpoint)
    {
        var uri = new Uri(endpoint);
        string[] listed = [uri.IdnHost, "169.254.169.254", "*"];
        foreach (string provider in AiProviders.All)
        {
            Assert.NotNull(AiEndpointPolicy.CheckEndpoint(provider, uri, s_none));
            Assert.NotNull(AiEndpointPolicy.CheckEndpoint(provider, uri, listed));
        }
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://[::ffff:127.0.0.1]:11434")]
    [InlineData("http://10.1.2.3:11434")]
    [InlineData("http://192.168.1.20:8000/v1")]
    [InlineData("http://172.16.5.5")]
    [InlineData("http://100.64.1.2:11434")]
    [InlineData("http://[fd12:3456::1]:11434")]
    [InlineData("http://ollama.internal:11434")] // a name: its addresses are judged when it is connected to
    [InlineData("https://ollama.lan")]
    public void Ollama_may_use_loopback_and_private_addresses_with_no_listing(string endpoint)
    {
        Assert.Null(AiEndpointPolicy.CheckEndpoint(AiProviders.Ollama, new Uri(endpoint), s_none));
    }

    [Fact]
    public void Ollama_may_not_use_a_public_address_unless_the_file_lists_it()
    {
        var literal = new Uri("http://203.0.113.7:11434");

        Assert.Equal(AiEndpointPolicy.OllamaPublicMessage, AiEndpointPolicy.CheckEndpoint(AiProviders.Ollama, literal, s_none));
        Assert.Null(AiEndpointPolicy.CheckEndpoint(AiProviders.Ollama, literal, ["203.0.113.7"]));
        Assert.NotNull(AiEndpointPolicy.CheckEndpoint(AiProviders.Ollama, literal, ["203.0.113.8"]));
        Assert.NotNull(AiEndpointPolicy.CheckEndpoint(AiProviders.Ollama, new Uri("http://[2606:4700:4700::1111]:11434"), s_none));
    }

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("openai", "https://API.OpenAI.com/v1/")]
    [InlineData("openai", "https://api.openai.com:443/v1")]
    [InlineData("anthropic", "https://api.anthropic.com/v1")]
    [InlineData("azure-openai", "https://my-resource.openai.azure.com")]
    [InlineData("azure-openai", "https://a.b.openai.azure.com")]
    public void A_keyed_provider_may_use_its_published_host_with_no_listing(string provider, string endpoint)
    {
        Assert.Null(AiEndpointPolicy.CheckEndpoint(provider, new Uri(endpoint), s_none));
    }

    [Theory]
    [InlineData("openai", "https://evil.example.com/v1")]
    [InlineData("openai", "https://api.openai.com.evil.example.com/v1")] // the published name as a prefix
    [InlineData("openai", "https://evil-api.openai.com/v1")]
    [InlineData("openai", "https://api.openai.com@evil.example.com/v1")] // userinfo trick: the host is evil.example.com
    [InlineData("openai", "https://api.anthropic.com/v1")] // another provider's published host
    [InlineData("openai", "https://203.0.113.7/v1")]
    [InlineData("openai", "https://localhost:8080/v1")]
    [InlineData("openai", "https://10.0.0.5/v1")]
    [InlineData("anthropic", "https://api.openai.com/v1")]
    [InlineData("anthropic", "https://proxy.example.com/v1")]
    [InlineData("anthropic", "https://api.anthropic.com.evil.example.com/")]
    [InlineData("azure-openai", "https://evil.example.com")]
    [InlineData("azure-openai", "https://openai.azure.com")] // no resource label
    [InlineData("azure-openai", "https://my-resource.openai.azure.com.evil.example.com")]
    [InlineData("azure-openai", "https://my-resource-openai.azure.com")]
    [InlineData("azure-openai", "https://my-resource.openai.azure.net")]
    public void A_keyed_provider_may_not_use_any_other_host_unless_the_file_lists_it(string provider, string endpoint)
    {
        var uri = new Uri(endpoint);

        string? refusal = AiEndpointPolicy.CheckEndpoint(provider, uri, s_none);

        Assert.NotNull(refusal);
        Assert.Contains("ai.allowedEndpointHosts", refusal, StringComparison.Ordinal);
        // Fixed text: the host that was refused is never echoed (only the provider's own published host, which the text names on purpose).
        string withoutPublished = refusal.Replace("*.openai.azure.com", string.Empty, StringComparison.Ordinal).Replace("api.openai.com", string.Empty, StringComparison.Ordinal).Replace("api.anthropic.com", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(uri.Host, withoutPublished, StringComparison.OrdinalIgnoreCase);
        // Listing it is what lifts the refusal, and only for what is listed.
        Assert.Null(AiEndpointPolicy.CheckEndpoint(provider, uri, [uri.IdnHost]));
        Assert.NotNull(AiEndpointPolicy.CheckEndpoint(provider, uri, ["somewhere-else.example.org"]));
    }

    [Theory]
    [InlineData("llm.corp.example", "llm.corp.example", true)]
    [InlineData("LLM.Corp.Example", "llm.corp.example", true)]
    [InlineData(" llm.corp.example ", "llm.corp.example", true)]
    [InlineData("*.corp.example", "llm.corp.example", true)]
    [InlineData("*.corp.example", "a.b.corp.example", true)]
    [InlineData("*.corp.example", "corp.example", false)] // a wildcard is for subdomains, not the domain itself
    [InlineData("*.corp.example", "evilcorp.example", false)]
    [InlineData("*.corp.example", "llm.corp.example.evil.test", false)]
    [InlineData("llm.corp.example", "other.corp.example", false)]
    [InlineData("llm.corp.example", "llm.corp.example.evil.test", false)]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("[::1]", "::1", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("*", "anything.example", false)] // a bare * is not an allow-list entry
    [InlineData("*.", "x.", false)]
    [InlineData("", "", false)]
    public void An_allow_list_entry_matches_exactly_or_as_a_subdomain_wildcard_and_nothing_else(string entry, string host, bool expected)
    {
        Assert.Equal(expected, AiEndpointPolicy.IsListed(host, [entry]));
    }

    [Theory]
    [InlineData("llm.corp.example", true)]
    [InlineData("*.corp.example", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("*", false)]
    [InlineData("https://llm.corp.example/v1", false)]
    [InlineData("llm.corp.example/v1", false)]
    [InlineData("llm.corp.example:8000", false)]
    [InlineData("user@llm.corp.example", false)]
    [InlineData("a b", false)]
    [InlineData("llm.*.example", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Only_a_host_name_an_ip_address_or_a_subdomain_wildcard_is_a_usable_allow_list_entry(string entry, bool usable)
    {
        Assert.Equal(usable, AiEndpointPolicy.IsUsableEntry(entry));
    }

    [Theory]
    [InlineData("openai", "https://llm.corp.example/v1", true)]
    [InlineData("openai", "http://localhost:8080/v1", true)]
    [InlineData("openai", "http://localhost.:8080/v1", false)] // not the name localhost
    [InlineData("openai", "http://foo.localhost:8080/v1", true)]
    [InlineData("openai", "http://127.0.0.1:8080/v1", true)]
    [InlineData("openai", "http://127.9.9.9:8080/v1", true)]
    [InlineData("openai", "http://[::1]:8080/v1", true)]
    [InlineData("openai", "http://[::ffff:127.0.0.1]:8080/v1", true)]
    [InlineData("openai", "http://llm.corp.example/v1", false)]
    [InlineData("openai", "http://10.0.0.5:8000/v1", false)]
    [InlineData("openai", "http://localhost.evil.example/v1", false)]
    [InlineData("anthropic", "http://llm.corp.example/v1", false)]
    [InlineData("azure-openai", "http://my-resource.openai.azure.com", false)]
    [InlineData("anthropic", "https://llm.corp.example/v1", true)]
    public void Plain_http_with_a_key_is_refused_unless_the_host_is_loopback(string provider, string endpoint, bool allowed)
    {
        var uri = new Uri(endpoint);
        string[] listed = ["llm.corp.example", "10.0.0.5", "localhost", "localhost.", "foo.localhost", "127.0.0.1", "127.9.9.9", "::1", "::ffff:127.0.0.1", "localhost.evil.example", "my-resource.openai.azure.com"];

        string? refusal = AiEndpointPolicy.CheckEndpoint(provider, uri, listed);

        Assert.Equal(allowed, refusal is null);
        Assert.Equal(allowed, AiEndpointPolicy.MayCarryKey(uri));
    }

    [Fact]
    public void Ollama_over_plain_http_to_a_private_host_is_allowed_but_would_not_carry_a_key()
    {
        var uri = new Uri("http://ollama.internal:11434");

        Assert.Null(AiEndpointPolicy.CheckEndpoint(AiProviders.Ollama, uri, s_none));
        Assert.False(AiEndpointPolicy.MayCarryKey(uri));
        Assert.True(AiEndpointPolicy.MayCarryKey(new Uri("https://ollama.internal:11434")));
        Assert.True(AiEndpointPolicy.MayCarryKey(new Uri("http://localhost:11434")));
    }

    [Fact]
    public void A_name_is_judged_by_everything_it_resolves_to_not_the_first_answer()
    {
        IPAddress[] good = [IPAddress.Parse("10.0.0.5")];
        IPAddress[] mixed = [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("169.254.169.254")];
        IPAddress[] mixedTheOtherWay = [IPAddress.Parse("169.254.169.254"), IPAddress.Parse("10.0.0.5")];
        IPAddress[] mappedMetadata = [IPAddress.Parse("::ffff:169.254.169.254")];

        Assert.Null(AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, hostIsListed: false, good));
        Assert.Equal(AiEndpointPolicy.BlockedMessage, AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, false, mixed));
        Assert.Equal(AiEndpointPolicy.BlockedMessage, AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, false, mixedTheOtherWay));
        Assert.Equal(AiEndpointPolicy.BlockedMessage, AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, false, mappedMetadata));
        // Listing the host never lifts a block, for any provider.
        foreach (string provider in AiProviders.All)
        {
            Assert.Equal(AiEndpointPolicy.BlockedMessage, AiEndpointPolicy.CheckAddresses(provider, hostIsListed: true, mixed));
            Assert.Equal(AiEndpointPolicy.BlockedMessage, AiEndpointPolicy.CheckAddresses(provider, hostIsListed: false, mixed));
        }
    }

    [Fact]
    public void An_unlisted_ollama_name_must_resolve_to_loopback_or_private_and_a_listed_one_may_resolve_anywhere_that_is_not_blocked()
    {
        IPAddress[] publicAddress = [IPAddress.Parse("203.0.113.7")];
        IPAddress[] loopback = [IPAddress.Loopback, IPAddress.IPv6Loopback];
        IPAddress[] privateAddress = [IPAddress.Parse("192.168.1.9"), IPAddress.Parse("fd12::1")];

        Assert.Equal(AiEndpointPolicy.OllamaPublicMessage, AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, false, publicAddress));
        Assert.Equal(AiEndpointPolicy.OllamaPublicMessage, AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, false, [.. privateAddress, .. publicAddress]));
        Assert.Null(AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, false, loopback));
        Assert.Null(AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, false, privateAddress));
        Assert.Null(AiEndpointPolicy.CheckAddresses(AiProviders.Ollama, hostIsListed: true, publicAddress));
        // A keyed provider is held to its host name, not its address: a published or listed host may be anywhere that is not blocked.
        Assert.Null(AiEndpointPolicy.CheckAddresses(AiProviders.OpenAi, false, publicAddress));
        Assert.Null(AiEndpointPolicy.CheckAddresses(AiProviders.OpenAi, true, privateAddress));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "api.openai.com:443")]
    [InlineData("https://API.OPENAI.COM:443/v1", "api.openai.com:443")]
    [InlineData("http://localhost:11434", "localhost:11434")]
    [InlineData("http://LocalHost/", "localhost:80")]
    [InlineData("http://[::1]:11434", "::1:11434")]
    [InlineData("https://proxy.corp.example:8443/v1", "proxy.corp.example:8443")]
    public void The_host_that_is_compared_is_the_lower_case_host_and_the_effective_port(string endpoint, string authority)
    {
        Assert.Equal(authority, AiEndpointPolicy.AuthorityOf(new Uri(endpoint)));
        Assert.Equal(authority, AiEndpointPolicy.AuthorityOf(endpoint));
    }

    [Fact]
    public void A_different_host_or_port_is_a_different_authority_and_the_scheme_and_path_are_not_part_of_it()
    {
        Assert.NotEqual(AiEndpointPolicy.AuthorityOf("https://proxy.corp.example/v1"), AiEndpointPolicy.AuthorityOf("https://proxy.corp.example:8443/v1"));
        Assert.NotEqual(AiEndpointPolicy.AuthorityOf("https://a.corp.example/v1"), AiEndpointPolicy.AuthorityOf("https://b.corp.example/v1"));
        Assert.Equal(AiEndpointPolicy.AuthorityOf("https://proxy.corp.example/v1"), AiEndpointPolicy.AuthorityOf("https://proxy.corp.example/v2/other"));
        Assert.Equal(string.Empty, AiEndpointPolicy.AuthorityOf(string.Empty));
        Assert.Equal(string.Empty, AiEndpointPolicy.AuthorityOf("not a url"));
    }
}
