using Microsoft.EntityFrameworkCore;

namespace SmartAgri.Infrastructure;

/// <summary>
/// The application's single EF Core context (see backend-stack ADR: PostgreSQL is the
/// single store). Milestone 1 slice 2 only needs the database to exist and the
/// <c>vector</c> extension to be enabled ahead of the pgvector tables that ship in M2;
/// no entities are mapped yet.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Reserved for M2's pgvector-backed embedding tables. No vector columns are
        // created yet; this only makes `CREATE EXTENSION IF NOT EXISTS vector` part of
        // the first migration.
        modelBuilder.HasPostgresExtension("vector");
    }
}
