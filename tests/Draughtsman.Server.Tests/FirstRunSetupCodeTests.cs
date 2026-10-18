using System.Net;
using System.Net.Http.Json;
using Draughtsman.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The setup code is what a person copies out of a terminal and types into a page whose button already enables on the trimmed value:
/// stray whitespace and letter case must not make a right code wrong, and a mistake about something else (a password too short) must
/// not spend the five-guess budget a wrong code is charged against.
/// </summary>
public class FirstRunSetupCodeTests
{
    private const string Password = "correcthorsebattery";

    [Theory]
    [InlineData("{0} ")]
    [InlineData(" {0}")]
    [InlineData("{0}\n")]
    [InlineData("\t{0}\r\n")]
    [InlineData("{1}")]
    [InlineData(" {1} ")]
    public void The_gate_accepts_the_right_code_however_it_was_pasted(string shape)
    {
        var gate = new SetupCodeGate();
        string code = gate.Issue();

        string candidate = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, code, code.ToLowerInvariant());

        Assert.True(gate.Verify(candidate), $"'{candidate}' should open the gate for {code}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WRONGCODE1")]
    public void The_gate_still_refuses_a_wrong_or_empty_code(string candidate)
    {
        var gate = new SetupCodeGate();
        gate.Issue();

        Assert.False(gate.Verify(candidate));
    }

    [Fact]
    public void The_gate_refuses_a_code_that_differs_in_the_middle_even_after_trimming_and_case_folding()
    {
        var gate = new SetupCodeGate();
        string code = gate.Issue();
        char[] chars = code.ToCharArray();
        chars[4] = chars[4] == 'A' ? 'B' : 'A';

        Assert.False(gate.Verify(" " + new string(chars).ToLowerInvariant() + " "));
        Assert.False(gate.Verify(code + "X"));
        Assert.False(gate.Verify(code[..^1]));
    }

    [Theory]
    [InlineData("{0} ")]
    [InlineData(" {0}")]
    [InlineData("{0}\n")]
    [InlineData("{1}")]
    public async Task First_run_creates_the_administrator_when_the_right_code_arrives_padded_or_in_lower_case(string shape)
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string code = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        string sent = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, code, code.ToLowerInvariant());

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "pasted@example.com", displayName = "Pasted", password = Password, setupCode = sent });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Mistakes_about_the_password_do_not_spend_the_guess_budget_of_a_right_code()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string code = factory.Services.GetRequiredService<SetupCodeGate>().Issue();

        // Seven times: more than the five guesses a wrong code is allowed in a window.
        for (int i = 0; i < 7; i++)
        {
            HttpResponseMessage tooShort = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "short@example.com", displayName = "Short", password = "too-short", setupCode = code });
            Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        }

        HttpResponseMessage valid = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "short@example.com", displayName = "Short", password = Password, setupCode = code });
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_still_spend_the_budget_and_then_even_the_right_code_waits()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string code = factory.Services.GetRequiredService<SetupCodeGate>().Issue();

        for (int i = 0; i < 5; i++)
        {
            HttpResponseMessage wrong = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "w@example.com", displayName = "W", password = Password, setupCode = "WRONGCODE" + i });
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        HttpResponseMessage blocked = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "w@example.com", displayName = "W", password = Password, setupCode = code });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task A_right_code_between_wrong_ones_does_not_buy_back_the_wrong_ones()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string code = factory.Services.GetRequiredService<SetupCodeGate>().Issue();

        // Four wrong guesses, then a right code with a bad password (refunded), then wrong guesses: the fifth wrong one in the window blocks.
        for (int i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/api/auth/first-run", new { email = "w@example.com", displayName = "W", password = Password, setupCode = "WRONGCODE" + i });
        }

        HttpResponseMessage rightButShort = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "w@example.com", displayName = "W", password = "short", setupCode = code });
        Assert.Equal(HttpStatusCode.BadRequest, rightButShort.StatusCode);

        HttpResponseMessage fifthWrong = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "w@example.com", displayName = "W", password = Password, setupCode = "WRONGCODE9" });
        Assert.Equal(HttpStatusCode.Forbidden, fifthWrong.StatusCode);

        HttpResponseMessage blocked = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "w@example.com", displayName = "W", password = Password, setupCode = code });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
}
