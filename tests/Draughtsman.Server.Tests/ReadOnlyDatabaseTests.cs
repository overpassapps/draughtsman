using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A database the server can open and read but not write (a restore or a copy made as another account): health used to say ok, a wrong password
/// was a 401 and the right one a bare 500. Now health says degraded, and the right password is refused with a problem that says the instance cannot save.
/// </summary>
public sealed class ReadOnlyDatabaseTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Health_turns_degraded_while_the_database_cannot_be_written_and_recovers_when_it_can()
    {
        var clock = new TestClock(Start);
        using var factory = new ResetServerFactory(clock: clock);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        Assert.Equal("ok", await Status(factory.CreateClient()));

        using (MakeReadOnly(factory))
        {
            clock.Advance(TimeSpan.FromMinutes(5));

            Assert.Equal("degraded", await Status(factory.CreateClient()));
            JsonObject detail = (await (await admin.GetAsync("/api/health")).Json()).AsObject();
            Assert.Equal("degraded", detail["status"]!.GetValue<string>());
            Assert.False(detail["storageWritable"]!.GetValue<bool>());
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        JsonObject healed = (await (await admin.GetAsync("/api/health")).Json()).AsObject();
        Assert.True(healed["status"]!.GetValue<string>() == "ok", healed.ToJsonString());
        Assert.Equal("ok", await Status(factory.CreateClient()));
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_correct_password_gets_a_problem_that_says_the_instance_cannot_save_and_a_wrong_one_is_still_a_401()
    {
        using var factory = new ResetServerFactory(clock: new TestClock(Start));
        await factory.CreateAuthenticatedClient();
        using IDisposable restore = MakeReadOnly(factory);
        HttpClient anonymous = factory.CreateClient();

        HttpResponseMessage wrong = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = "not-the-password-at-all" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        HttpResponseMessage right = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, right.StatusCode);
        Assert.Equal("application/problem+json", right.Content.Headers.ContentType?.MediaType);
        JsonObject problem = (await right.Json()).AsObject();
        Assert.Equal(503, problem["status"]!.GetValue<int>());
        string detail = problem["detail"]!.GetValue<string>();
        Assert.Contains("cannot save", detail, StringComparison.OrdinalIgnoreCase);
        // The page shows this to anybody who types a password: it must not hand over the data folder or the exception.
        Assert.DoesNotContain(factory.DataFolder, detail, StringComparison.Ordinal);
        string raw = await right.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SqliteException", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_database_that_can_be_written_a_correct_password_signs_in()
    {
        using var factory = new ResetServerFactory(clock: new TestClock(Start));
        await factory.CreateAuthenticatedClient();

        HttpResponseMessage right = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });

        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    private static async Task<string?> Status(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Json())["status"]!.GetValue<string>();
    }

    /// <summary>
    /// Releases the server's pooled connections and takes the write bit off data.db, so the next connection opens read-only as it does for a file restored by
    /// another account. Dispose puts the bit back.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static IDisposable MakeReadOnly(DraughtsmanServerFactory factory)
    {
        SqlitePools.Release(factory.DataFolder);
        string file = Path.Combine(factory.DataFolder, "data.db");
        UnixFileMode before = File.GetUnixFileMode(file);
        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        return new Restore(factory.DataFolder, file, before);
    }

    [UnsupportedOSPlatform("windows")]
    private sealed class Restore(string dataFolder, string file, UnixFileMode mode) : IDisposable
    {
        public void Dispose()
        {
            // SQLite gives the -wal and -shm files it creates the main file's mode, so the ones made while it was read-only are read-only too.
            foreach (string sibling in Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + "-*"))
            {
                File.SetUnixFileMode(sibling, mode);
            }

            File.SetUnixFileMode(file, mode);
            // A connection opened while the file was read-only stays read-only; the server's pool must hand out new ones, as a restart does.
            SqlitePools.Release(dataFolder);
        }
    }
}
