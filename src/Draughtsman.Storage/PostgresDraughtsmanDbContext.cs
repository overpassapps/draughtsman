using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Storage;

/// <summary>Postgres flavour; its migrations live in Migrations/Postgres.</summary>
public sealed class PostgresDraughtsmanDbContext(DbContextOptions<PostgresDraughtsmanDbContext> options) : DraughtsmanDbContext(options)
{
}
