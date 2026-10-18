using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Storage;

/// <summary>
/// Wires the chosen provider into DI. The rest of the application depends on <see cref="IStore"/> only; the context
/// registration is an implementation detail of this assembly.
/// </summary>
public static class StorageSetup
{
    public static IServiceCollection AddDraughtsmanStorage(this IServiceCollection services, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        switch (options.Provider)
        {
            case StorageProvider.Sqlite:
                services.AddDbContext<DraughtsmanDbContext, SqliteDraughtsmanDbContext>(builder => ConfigureSqlite(builder, options.ConnectionString));
                break;
            case StorageProvider.Postgres:
                services.AddDbContext<DraughtsmanDbContext, PostgresDraughtsmanDbContext>(builder => ConfigurePostgres(builder, options.ConnectionString));
                break;
            default:
                throw new InvalidOperationException($"Unsupported storage provider {options.Provider}.");
        }

        services.AddScoped<IStore, EfStore>();
        services.AddScoped<IUserStore, EfUserStore>();
        services.AddScoped<IPasswordResetStore, EfPasswordResetStore>();
        services.AddScoped<IStoreMaintenance, EfStoreMaintenance>();
        services.AddScoped<ISecurityEventStore, EfSecurityEventStore>();
        return services;
    }

    public static DbContextOptions<SqliteDraughtsmanDbContext> BuildSqliteOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<SqliteDraughtsmanDbContext>();
        ConfigureSqlite(builder, connectionString);
        return builder.Options;
    }

    public static DbContextOptions<PostgresDraughtsmanDbContext> BuildPostgresOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<PostgresDraughtsmanDbContext>();
        ConfigurePostgres(builder, connectionString);
        return builder.Options;
    }

    /// <summary>Creates an unpooled context for the given options; used by the migrator and the provider test suite.</summary>
    public static DraughtsmanDbContext CreateContext(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Provider switch
        {
            StorageProvider.Sqlite => new SqliteDraughtsmanDbContext(BuildSqliteOptions(options.ConnectionString)),
            StorageProvider.Postgres => new PostgresDraughtsmanDbContext(BuildPostgresOptions(options.ConnectionString)),
            _ => throw new InvalidOperationException($"Unsupported storage provider {options.Provider}."),
        };
    }

    private static void ConfigureSqlite(DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(StorageSetup).Assembly.GetName().Name));
    }

    private static void ConfigurePostgres(DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(StorageSetup).Assembly.GetName().Name));
    }
}
