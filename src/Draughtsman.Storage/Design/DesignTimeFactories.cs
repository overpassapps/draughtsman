using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Draughtsman.Storage.Design;

// Used only by `dotnet ef migrations add`, which needs a context but never opens the connection. Regenerate with:
//   dotnet ef migrations add <Name> --project src/Draughtsman.Storage --context SqliteDraughtsmanDbContext --output-dir Migrations/Sqlite
//   dotnet ef migrations add <Name> --project src/Draughtsman.Storage --context PostgresDraughtsmanDbContext --output-dir Migrations/Postgres

public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteDraughtsmanDbContext>
{
    public SqliteDraughtsmanDbContext CreateDbContext(string[] args)
    {
        return new SqliteDraughtsmanDbContext(StorageSetup.BuildSqliteOptions("Data Source=design-time.db"));
    }
}

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresDraughtsmanDbContext>
{
    public PostgresDraughtsmanDbContext CreateDbContext(string[] args)
    {
        return new PostgresDraughtsmanDbContext(StorageSetup.BuildPostgresOptions("Host=localhost;Database=draughtsman_design_time"));
    }
}
