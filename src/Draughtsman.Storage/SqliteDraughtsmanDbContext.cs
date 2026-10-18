using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Storage;

/// <summary>SQLite flavour; its migrations live in Migrations/Sqlite.</summary>
public sealed class SqliteDraughtsmanDbContext(DbContextOptions<SqliteDraughtsmanDbContext> options) : DraughtsmanDbContext(options)
{
}
