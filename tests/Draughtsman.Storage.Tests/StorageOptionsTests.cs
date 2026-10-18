namespace Draughtsman.Storage.Tests;

public sealed class StorageOptionsTests
{
    private static readonly string s_dataFolder = Path.Combine(Path.GetTempPath(), "draughtsman-data");

    [Theory]
    [InlineData("")]
    [InlineData("sqlite")]
    [InlineData("SQLite")]
    public void Empty_or_sqlite_provider_defaults_to_data_db_in_the_data_folder(string provider)
    {
        StorageOptions options = StorageOptions.FromConfig(provider, string.Empty, s_dataFolder);

        Assert.Equal(StorageProvider.Sqlite, options.Provider);
        Assert.Equal("sqlite", options.ProviderName);
        Assert.Equal($"Data Source={Path.Combine(s_dataFolder, "data.db")}", options.ConnectionString);
    }

    [Fact]
    public void Explicit_sqlite_connection_string_wins()
    {
        StorageOptions options = StorageOptions.FromConfig("sqlite", "Data Source=elsewhere.db", s_dataFolder);

        Assert.Equal("Data Source=elsewhere.db", options.ConnectionString);
    }

    [Fact]
    public void Postgres_needs_a_connection_string()
    {
        const string provider = "postgres";
        StorageOptions options = StorageOptions.FromConfig(provider, " Host=db;Database=draughtsman ", s_dataFolder);

        Assert.Equal(StorageProvider.Postgres, options.Provider);
        Assert.Equal("postgres", options.ProviderName);
        Assert.Equal("Host=db;Database=draughtsman", options.ConnectionString);
        Assert.Throws<InvalidOperationException>(() => StorageOptions.FromConfig(provider, string.Empty, s_dataFolder));
    }

    [Fact]
    public void Unknown_provider_is_an_error_not_a_fallback()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => StorageOptions.FromConfig("sqlserver", "x", s_dataFolder));

        Assert.Contains("sqlserver", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("npgsql")]
    public void Only_the_documented_postgres_spelling_is_accepted(string provider)
    {
        Assert.Throws<InvalidOperationException>(() => StorageOptions.FromConfig(provider, "Host=db", s_dataFolder));
    }
}
