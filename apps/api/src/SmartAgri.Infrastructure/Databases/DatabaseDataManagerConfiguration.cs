using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Infrastructure.Databases;

/// <summary>
/// Table <c>DatabaseDataManagers</c> (M4 #144). Both foreign keys include
/// <c>OrganizationId</c>, so a designation can only ever name an account of the database's own
/// organization even if application code got it wrong; both cascade (deleting the database or
/// the account removes the designation).
/// </summary>
internal sealed class DatabaseDataManagerConfiguration : IEntityTypeConfiguration<DatabaseDataManager>
{
    public void Configure(EntityTypeBuilder<DatabaseDataManager> builder)
    {
        builder.ToTable("DatabaseDataManagers");
        builder.HasKey(manager => new { manager.DatabaseId, manager.AccountId });

        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(manager => new { manager.DatabaseId, manager.OrganizationId })
            .HasPrincipalKey(database => new { database.Id, database.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(manager => new { manager.AccountId, manager.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // "Which databases does this account manage": the list and the read check.
        builder.HasIndex(manager => manager.AccountId);
    }
}

/// <summary>Table <c>DatabaseDataManagerChanges</c>: append-only history of designations.</summary>
internal sealed class DatabaseDataManagerChangeConfiguration : IEntityTypeConfiguration<DatabaseDataManagerChange>
{
    public void Configure(EntityTypeBuilder<DatabaseDataManagerChange> builder)
    {
        builder.ToTable("DatabaseDataManagerChanges");
        builder.HasKey(change => change.Id);
        builder.Property(change => change.Id).ValueGeneratedNever();

        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(change => new { change.DatabaseId, change.OrganizationId })
            .HasPrincipalKey(database => new { database.Id, database.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(change => new { change.DatabaseId, change.ChangedAt });
    }
}
