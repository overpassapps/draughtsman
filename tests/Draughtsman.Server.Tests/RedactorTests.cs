using System.Globalization;
using System.Text;
using Draughtsman.Server.Diagnostics;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The redactor is what stands between a customer's log and an email to us, so it is tested from both sides: secrets of many shapes, in many
/// contexts, must not survive (and not partially), and ordinary diagnostic text must, or a redactor that blanks everything would pass. The
/// property tests use seeded random secrets: <c>DRAUGHTSMAN_FUZZ_ITERATIONS</c> (the same variable the DSL fuzz test reads) raises the count.
/// </summary>
public class RedactorTests
{
    private static readonly int s_iterations = int.TryParse(Environment.GetEnvironmentVariable("DRAUGHTSMAN_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out int n) && n > 0 ? Math.Max(n / 10, 500) : 500;

    // ------------------------------------------------------------------------------------------------------ planted secrets, by hand

    public static TheoryData<string, string> PlantedSecrets()
    {
        // (line, the secret that must not survive it)
        return new TheoryData<string, string>
        {
            { "password=Hunter2-hunter2", "Hunter2-hunter2" },
            { "Password: correct horse battery staple", "correct horse battery staple" },
            { "login failed for user bob, password = p@ss w0rd!", "p@ss w0rd!" },
            { "{\"email\":\"bob@example.com\",\"password\":\"S3cr3t Pass,phrase\",\"remember\":true}", "S3cr3t Pass,phrase" },
            { "smtp.password: SmtpPassw0rdForTests", "SmtpPassw0rdForTests" },
            { "DRAUGHTSMAN_SMTP_PASSWORD=mailPass-9x8y7z", "mailPass-9x8y7z" },
            { "ai.apiKey = sk-" + "test-abcdefghijklmnopqrstuvwxyz0123456789", "sk-" + "test-abcdefghijklmnopqrstuvwxyz0123456789" },
            { "AI key: ZzYyXx1234567890Plain", "ZzYyXx1234567890Plain" },
            { "X-Api-Key: abcd1234efgh5678", "abcd1234efgh5678" },
            { "Authorization: Bearer eyJ" + "hbGciOiJIUzI1NiJ9.eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk" },
            { "authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA" },
            { "request carried bearer dst_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCd", "dst_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCd" },
            { "Cookie: ds_session=Zk3-8vQnLw2pYx9TfR7sHd1MaE6cUo5BjG4iNy0Ve_A; XSRF-TOKEN=Qq1Ww2Ee3Rr4Tt5Yy6Uu7Ii8Oo9Pp0Aa1S", "Zk3-8vQnLw2pYx9TfR7sHd1MaE6cUo5BjG4iNy0Ve_A" },
            { "Set-Cookie: __Host-ds_session=SESSIONSECRETVALUE0123456789abcdef; path=/; secure; httponly", "SESSIONSECRETVALUE0123456789abcdef" },
            { "ds_session=Zk3-8vQnLw2pYx9TfR7sHd1MaE6cUo5BjG4iNy0Ve_A", "Zk3-8vQnLw2pYx9TfR7sHd1MaE6cUo5BjG4iNy0Ve_A" },
            { "XSRF-TOKEN=Qq1Ww2Ee3Rr4Tt5Yy6Uu7Ii8Oo9Pp0Aa1S", "Qq1Ww2Ee3Rr4Tt5Yy6Uu7Ii8Oo9Pp0Aa1S" },
            { "First-run setup code (single use): K7M2QXPV4N", "K7M2QXPV4N" },
            { "setup code is K7M2QXPV4N", "K7M2QXPV4N" },
            { "the code K7M2QXPV4N was printed", "K7M2QXPV4N" },
            { "API token created: dst_Wm3nB8vC1xZ4aS7dF0gH2jK5lQ9eR6tY-uIoPLkJhGf", "dst_Wm3nB8vC1xZ4aS7dF0gH2jK5lQ9eR6tY-uIoPLkJhGf" },
            { "reset link https://draughtsman.example.com/reset-password#token=dpr_Wm3nB8vC1xZ4aS7dF0gH2jK5lQ9eR6tY-uIoPLkJhGf", "dpr_Wm3nB8vC1xZ4aS7dF0gH2jK5lQ9eR6tY-uIoPLkJhGf" },
            { "connection string: Host=db.internal;Username=draught;Password=Pg-Secret-9f8e7d6c;Database=draught", "Pg-Secret-9f8e7d6c" },
            { "storage.connectionString = Host=db;Password=Pg-Secret-9f8e7d6c", "Pg-Secret-9f8e7d6c" },
            { "fetching https://svc_user:Url-P4ssword-xyz@internal.example.com/feed", "Url-P4ssword-xyz" },
            { "fetching https://onlyuser-abc123@internal.example.com/feed", "onlyuser-abc123" },
            { "GET /api/x?token=Qw3rTy-7u1Op9aSd&other=1 200", "Qw3rTy-7u1Op9aSd" },
            { "key sk-ant-" + "api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEfGhIjKlMnOpQrStUvWxYz-AAAA used", "sk-ant-" + "api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEfGhIjKlMnOpQrStUvWxYz-AAAA" },
            { "aws AKIA" + "IOSFODNN7EXAMPLE configured", "AKIA" + "IOSFODNN7EXAMPLE" },
            { "github ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 pushed", "ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789" },
            { "slack xox" + "b-123456789012-abcdefghijklmnop leaked", "xox" + "b-123456789012-abcdefghijklmnop" },
            { "jwt eyJ" + "hbGciOiJIUzI1NiJ9.eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk seen", "eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0" },
            { "odd jwt-like abc123xyz.def456uvw.ghi789rst seen", "abc123xyz.def456uvw.ghi789rst" },
            { "sha256 of the key 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 stored", "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" },
            { "random 4Hk9QpLz3Vx8WnYb2TcR7sDf5GjM1aXe seen", "4Hk9QpLz3Vx8WnYb2TcR7sDf5GjM1aXe" },
            { "std base64 aB3+dE5/gH7=iJ9kL1MnO2pQr4StU6vWx8Yz seen", "aB3+dE5/gH7=iJ9kL1MnO2pQr4StU6vWx8Yz" },
            { "-----BEGIN PRIV" + "ATE KEY----- MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7", "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7" },
            { "session: 0f1e2d3c4b5a69788796a5b4c3d2e1f0", "0f1e2d3c4b5a69788796a5b4c3d2e1f0" },
            { "client_secret=abcDEF123ghiJKL456", "abcDEF123ghiJKL456" },
            { "the password is hunter2hunter2", "hunter2hunter2" },
            { "secret was OpenSesame-99", "OpenSesame-99" },
            { "licence key: LK-1234-5678-9012-ABCD", "LK-1234-5678-9012-ABCD" },
            { "encryption key = 0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef" },
        };
    }

    [Theory]
    [MemberData(nameof(PlantedSecrets))]
    public void A_planted_secret_does_not_survive(string line, string secret)
    {
        string redacted = Redactor.Redact(line);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        AssertNoLongWindow(secret, redacted);
        // Not a blanket: the line is still a line, and the redactor says it took something out.
        Assert.NotEmpty(redacted);
        Assert.NotEqual(line, redacted);
    }

    [Fact]
    public void An_email_address_becomes_its_first_character_and_its_domain_and_stays_that_way()
    {
        Assert.Equal("e***@example.com", Redactor.MaskEmail("eric@example.com"));
        Assert.Equal("login from e***@example.com failed", Redactor.Redact("login from eric@example.com failed"));
        Assert.Equal("a***@sub.example.co.uk and b***@x.org", Redactor.Redact("alice.smith+tag@sub.example.co.uk and bob@x.org"));
        Assert.Equal("e***@example.com", Redactor.Redact(Redactor.Redact("eric@example.com")));
        Assert.DoesNotContain("eric", Redactor.Redact("sign_in.failed for eric@example.com"), StringComparison.Ordinal);
    }

    [Fact]
    public void Addresses_are_cut_to_their_prefix_and_loopback_is_left_alone()
    {
        Assert.Equal("from 203.0.113.0/24 refused", Redactor.Redact("from 203.0.113.77 refused"));
        Assert.Equal("proxy 172.17.0.0/24 and 10.1.2.0/24", Redactor.Redact("proxy 172.17.0.1 and 10.1.2.3"));
        Assert.Equal("bound to 127.0.0.1 and 0.0.0.0 and ::1", Redactor.Redact("bound to 127.0.0.1 and 0.0.0.0 and ::1"));
        Assert.Equal("from 2001:db8:abcd:12::/64 refused", Redactor.Redact("from 2001:db8:abcd:12:34:56:78:9 refused"));
        Assert.Equal("fe80:0:0:0::/64", Redactor.Redact("fe80::1"));
        // Already a prefix: untouched, so redacting twice changes nothing.
        Assert.Equal("range 172.17.0.0/16 and 203.0.113.0/24", Redactor.Redact("range 172.17.0.0/16 and 203.0.113.0/24"));
        // Not addresses.
        Assert.Equal("build 1.2.3 of node 22.19.0 on port 5180", Redactor.Redact("build 1.2.3 of node 22.19.0 on port 5180"));
        Assert.Equal("at 09:00:00 and 12:34", Redactor.Redact("at 09:00:00 and 12:34"));
        Assert.Equal("std::string and Foo::Bar", Redactor.Redact("std::string and Foo::Bar"));
        Assert.Equal("203.0.113.0/24", Redactor.TruncateAddress("203.0.113.9"));
        Assert.Equal("127.0.0.1", Redactor.TruncateAddress("127.0.0.1"));
        Assert.Equal("not an address", Redactor.TruncateAddress("not an address"));
    }

    [Fact]
    public void Addresses_can_be_left_exact_for_a_settings_listing()
    {
        Assert.Equal("server.knownProxies: [172.17.0.1, 10.0.0.2]", Redactor.Redact("server.knownProxies: [172.17.0.1, 10.0.0.2]", truncateAddresses: false));
        // The same switch does not weaken anything else.
        Assert.DoesNotContain("hunter2hunter2", Redactor.Redact("password=hunter2hunter2 from 172.17.0.1", truncateAddresses: false), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ what must survive

    [Theory]
    [InlineData("2026-10-06T09:00:00.123Z INF Draughtsman.Server.Startup: Draughtsman 0.1.0; data folder /var/lib/draughtsman; storage sqlite")]
    [InlineData("2026-10-06T09:00:00.123Z INF Microsoft.Hosting.Lifetime: Now listening on: http://0.0.0.0:5180")]
    [InlineData("2026-10-06T09:00:00.123Z INF Draughtsman.Server.Security: Security event sign_in.failed: actor - target - from - via anonymous. wrong password")]
    [InlineData("2026-10-06T09:00:00.123Z INF Draughtsman.Server.Security: Security event password.changed: actor u_1 target u_1 from - via session. changed")]
    [InlineData("2026-10-06T09:00:00.123Z INF Draughtsman.Server.Security: Security event token.created: actor u_1 target - from - via session. ci")]
    [InlineData("2026-10-06T09:00:00.123Z WRN Draughtsman.Server.Auth.CookieSessionAuthenticationHandler: cookie handler warning")]
    [InlineData("   at Draughtsman.Server.Diagnostics.Redactor.RedactLine(String line) in /src/Draughtsman.Server/Diagnostics/Redactor.cs:line 120")]
    [InlineData("   at Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke(HttpContext httpContext)")]
    [InlineData("20260929120000_AddSecurityEvents")]
    [InlineData("data.db.pre-migration-20261006T120000Z")]
    [InlineData("20260930101500_AddPasswordResetTokensTable")]
    [InlineData("  [applied] 20261001093000_AddSecurityEventsAndIndexes")]
    [InlineData("backups/key-ring-20261006T120000Z/keys/key-1.xml and /var/lib/draughtsman-v2/backups/pre-restore-20261006-120000/")]
    [InlineData("draughtsman-support-bundle-20261006-120000.zip")]
    [InlineData("-rw------- draughtsman (uid 10001)   12.5 MiB  data.db")]
    [InlineData("/Users/someone/projects/draughtsman/data.db")]
    [InlineData("Host header received: draughtsman.example.com")]
    [InlineData("keyRing.protection: none")]
    [InlineData("server.forceHttps: true")]
    [InlineData("Free: 12.3 GiB, volume 100 GiB, uid 10001, mode 0700, exit code 2")]
    [InlineData("Result: 1 warning(s), no failures. Exit code 1.")]
    [InlineData("CookieSessionAuthenticationHandlerWithAVeryLongNameThatIsStillAName")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command executed in 4 ms")]
    public void Ordinary_diagnostic_text_is_left_as_it_is(string line)
    {
        Assert.Equal(line, Redactor.Redact(line));
    }

    [Fact]
    public void A_security_event_name_keeps_its_name_but_loses_a_secret_after_it()
    {
        string redacted = Redactor.Redact("Security event token.created: token=Qw3rTy7u1Op9aSdFg password=hunter2hunter2 done");

        Assert.StartsWith("Security event token.created: token=[redacted]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Qw3rTy7u1Op9aSdFg", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2hunter2", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_breaks_survive_and_a_secret_on_each_line_is_removed()
    {
        string text = "first line ok\r\npassword=aaaa-1111-bbbb\r\nthird line ok\npassword=cccc-2222-dddd\n";

        string redacted = Redactor.Redact(text);

        Assert.Equal(["first line ok\r", "password=[redacted]\r", "third line ok", "password=[redacted]", ""], redacted.Split('\n'));
    }

    [Fact]
    public void Redacting_twice_changes_nothing_for_every_planted_secret()
    {
        foreach (object[] row in PlantedSecrets())
        {
            string once = Redactor.Redact((string)row[0]);
            Assert.Equal(once, Redactor.Redact(once));
        }
    }

    [Fact]
    public void A_very_long_hostile_line_is_handled_in_linear_time_and_never_hangs()
    {
        // The patterns run on the non-backtracking engine, so this is a throughput check, not a hope.
        string hostile = string.Concat(Enumerable.Repeat("password=a", 20_000)) + new string(' ', 200_000) + string.Concat(Enumerable.Repeat("a1B2c3D4", 50_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        string redacted = Redactor.Redact(hostile);

        Assert.True(redacted.Length < hostile.Length, "something was removed");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20) * LoadScale.Now(), $"took {watch.Elapsed}");
    }

    // ------------------------------------------------------------------------------------------------------ property tests: random secrets

    /// <summary>Contexts in which a secret of ANY characters must go: a secret-looking name gives it away, whatever the secret looks like.</summary>
    private static readonly string[] s_keyedContexts =
    [
        "password={0}",
        "Password: {0}",
        "\"password\":\"{0}\"",
        "{{\"user\":\"u\",\"password\": \"{0}\",\"x\":1}}",
        "smtp.password = {0}",
        "DRAUGHTSMAN_SMTP_PASSWORD={0}",
        "apiKey: {0}",
        "api-key={0} extra",
        "token={0}",
        "Authorization: {0}",
        "Cookie: a=b; ds_session={0}",
        "Set-Cookie: x={0}; path=/",
        "the password is {0}",
        "secret was {0}",
        "setup code: {0}",
        "client_secret={0}",
        "connectionString={0}",
        "AI key: {0}",
        "2026-10-06T09:00:00.000Z WRN cat: failed with password={0} for someone",
    ];

    /// <summary>Contexts for secrets with the SHAPE of a token: nothing about the line gives them away, only what they look like.</summary>
    private static readonly string[] s_bareContexts =
    [
        "{0}",
        "value {0} here",
        "GET /api/reset/{0}/confirm 200",
        "{{\"x\":\"{0}\"}}",
        "id={0}&other=1",
        "from \"{0}\" to nowhere",
        "[{0}]",
    ];

    [Fact]
    public void A_random_secret_of_any_shape_does_not_survive_a_line_that_names_it_as_a_secret()
    {
        var random = new Random(20261006);
        for (int i = 0; i < s_iterations; i++)
        {
            string secret = PrintableSecret(random);
            foreach (string context in s_keyedContexts)
            {
                string line = string.Format(CultureInfo.InvariantCulture, context, secret);
                string redacted = Redactor.Redact(line);

                Assert.False(redacted.Contains(secret, StringComparison.Ordinal), $"secret survived in: {Visible(redacted)}\n  from: {Visible(line)}");
                AssertNoLongWindow(secret, redacted);
            }
        }
    }

    [Theory]
    [InlineData("hex")]
    [InlineData("hex-short")]
    [InlineData("base64")]
    [InlineData("base64url")]
    [InlineData("alnum")]
    [InlineData("jwt")]
    [InlineData("jwt-short")]
    [InlineData("api-token")]
    [InlineData("reset-link")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("aws")]
    [InlineData("github")]
    [InlineData("setup-code")]
    public void A_random_secret_with_the_shape_of_a_token_does_not_survive_anywhere_in_a_line(string shape)
    {
        var random = new Random(shape.GetHashCode(StringComparison.Ordinal) ^ 20261006);
        var survivors = new List<string>();
        for (int i = 0; i < s_iterations; i++)
        {
            string secret = ShapedSecret(shape, random);
            // The shape rules' contract: a random-looking run of 16 or more letters and digits that holds a digit. A random run of letters with no digit at all is the one
            // thing they cannot see (about 1 in 125 at 24 characters, 1 in 8,000 at 43); a token that names itself is caught by the keyed rules whatever it holds.
            if (NoDigitRandom(shape, secret))
            {
                continue;
            }

            foreach (string context in s_bareContexts)
            {
                string line = string.Format(CultureInfo.InvariantCulture, context, secret);
                string redacted = Redactor.Redact(line);
                if (redacted.Contains(secret, StringComparison.Ordinal) || HasLongWindow(secret, redacted))
                {
                    survivors.Add($"{shape} survived in: {Visible(redacted)}   (from: {Visible(line)})");
                    break;
                }
            }
        }

        // At the default count not one may survive. A longer run (DRAUGHTSMAN_FUZZ_ITERATIONS) may meet the rare token whose upper and lower case
        // happen to fall in long clusters (about 1 in 20,000 at 24 characters): up to 1 in 2,000 is tolerated, no more.
        int allowed = s_iterations / 2000;
        Assert.True(survivors.Count <= allowed, $"{survivors.Count} of {s_iterations} survived (allowed {allowed}):\n" + string.Join('\n', survivors.Take(5)));
    }

    [Fact]
    public void A_random_email_address_never_survives_whole_and_the_text_around_it_does()
    {
        var random = new Random(77);
        for (int i = 0; i < s_iterations; i++)
        {
            string local = Letters(random, random.Next(3, 12)) + (random.Next(2) == 0 ? "." + Letters(random, 4) : string.Empty);
            string domain = Letters(random, random.Next(3, 10)).ToLowerInvariant() + ".example.com";
            string email = $"{local}@{domain}";

            string redacted = Redactor.Redact($"sign-in by {email} failed twice");

            Assert.DoesNotContain(email, redacted, StringComparison.Ordinal);
            Assert.DoesNotContain(local[1..] + "@", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain(local[1..] + "***", redacted, StringComparison.Ordinal);
            Assert.Contains($"@{domain} failed twice", redacted, StringComparison.Ordinal);
            Assert.StartsWith($"sign-in by {local[0]}***@", redacted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Redaction_is_idempotent_for_random_text_with_and_without_secrets()
    {
        var random = new Random(4242);
        for (int i = 0; i < s_iterations; i++)
        {
            string line = string.Join(' ', Enumerable.Range(0, random.Next(1, 12)).Select(_ => random.Next(6) switch
            {
                0 => PrintableSecret(random),
                1 => ShapedSecret("base64", random),
                2 => $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}",
                3 => Letters(random, 5) + "@" + Letters(random, 6).ToLowerInvariant() + ".org",
                4 => "password=" + Letters(random, 8),
                _ => Letters(random, random.Next(2, 9)),
            }));

            string once = Redactor.Redact(line);

            Assert.Equal(once, Redactor.Redact(once));
        }
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private static string PrintableSecret(Random random)
    {
        // Printable ASCII, spaces and quotes included, 12 to 40 characters: a password a person might type.
        int length = random.Next(12, 41);
        var text = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            text.Append((char)random.Next(' ', '~' + 1));
        }

        // A secret that starts or ends with a space cannot be told from the separator; and one with a line break is not one line.
        return text.ToString().Trim().Length < 12 ? "Zq9-Xw8_Vb7=Nm6" : text.ToString().Trim();
    }

    private static string ShapedSecret(string shape, Random random)
    {
        const string Hex = "0123456789abcdef";
        const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        const string Base64Url = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const string SetupAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        switch (shape)
        {
            case "hex":
                return From(Hex, random.Next(32, 129), random);
            case "base64":
                return From(Base64, random.Next(24, 65), random) + (random.Next(2) == 0 ? "=" : string.Empty);
            case "base64url":
                return From(Base64Url, random.Next(24, 65), random);
            case "alnum":
                return From(Alnum, random.Next(24, 65), random);
            case "hex-short":
                return From(Hex, random.Next(20, 32), random);
            case "jwt":
                return "eyJ" + From(Base64Url, random.Next(10, 40), random) + "." + From(Base64Url, random.Next(10, 60), random) + "." + From(Base64Url, random.Next(20, 50), random);
            case "jwt-short":
                // Not a real JWT: three dotted runs with a digit in them, as a person writing one by hand makes them.
                return Letters(random, 5).ToLowerInvariant() + random.Next(10, 99) + "x." + Letters(random, 5).ToLowerInvariant() + random.Next(10, 99) + "y." + Letters(random, 5).ToLowerInvariant() + random.Next(10, 99) + "z";
            case "api-token":
                return "dst_" + From(Base64Url, 43, random);
            case "reset-link":
                return "dpr_" + From(Base64Url, 43, random);
            case "openai":
                return "sk-" + From(Alnum, random.Next(32, 60), random);
            case "anthropic":
                return "sk-ant-api03-" + From(Base64Url, random.Next(60, 100), random);
            case "aws":
                return "AKIA" + From("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", 16, random);
            case "github":
                return "ghp_" + From(Alnum, 36, random);
            case "setup-code":
                {
                    // Ten characters from the first-run alphabet, with at least a letter and a digit, as the real code is in all but about one case in a thousand.
                    string code;
                    do
                    {
                        code = From(SetupAlphabet, 10, random);
                    }
                    while (!code.Any(char.IsAsciiDigit) || !code.Any(char.IsAsciiLetter));

                    return code;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static string From(string alphabet, int length, Random random)
    {
        return new string([.. Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)])]);
    }

    private static string Letters(Random random, int length)
    {
        return From("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", length, random);
    }

    private static bool HasLongWindow(string secret, string redacted)
    {
        const int Window = 6;
        for (int i = 0; i + Window <= secret.Length; i++)
        {
            string piece = secret.Substring(i, Window);
            if (piece.Trim().Length == Window && redacted.Contains(piece, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A secret that survived in part is a leak too: no run of six of its characters may remain.</summary>
    private static void AssertNoLongWindow(string secret, string redacted)
    {
        const int Window = 6;
        for (int i = 0; i + Window <= secret.Length; i++)
        {
            string piece = secret.Substring(i, Window);
            if (piece.Trim().Length < Window)
            {
                continue; // spaces and nothing else of substance
            }

            Assert.False(redacted.Contains(piece, StringComparison.Ordinal), $"part of the secret survived: '{piece}' in {Visible(redacted)}");
        }
    }

    private static bool NoDigitRandom(string shape, string secret)
    {
        return shape is "alnum" or "base64" or "base64url" && !secret.Any(char.IsAsciiDigit);
    }

    private static string Visible(string text)
    {
        return text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
