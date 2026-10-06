using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Databases;

/// <summary>
/// Table <c>Databases</c>. Organization filter, <c>Organizations</c> foreign key and
/// concurrency token come from <see cref="AppDbContext"/>'s organization scope, like every
/// <c>IOrganizationScoped</c> entity.
/// </summary>
internal sealed class DatabaseConfiguration : IEntityTypeConfiguration<Database>
{
    public void Configure(EntityTypeBuilder<Database> builder)
    {
        builder.ToTable("Databases");
        builder.HasKey(database => database.Id);
        builder.Property(database => database.Id).ValueGeneratedNever();
        builder.Property(database => database.Name).HasMaxLength(Database.NameMaxLength).IsRequired();
        builder.Property(database => database.Purpose).HasMaxLength(Database.PurposeMaxLength).IsRequired();
        builder.Property(database => database.TemplateId)
            .HasConversion<WireNameConverter<DatabaseTemplateId>>()
            .HasMaxLength(64)
            .IsRequired();
        builder.Ignore(database => database.IsArchived);

        // Target of the composite foreign keys from the database's own rows (form versions now;
        // data managers, submissions and connections in #144–#148), so the database refuses a
        // child row whose organization differs from its database's.
        builder.HasAlternateKey(database => new { database.Id, database.OrganizationId });

        // The owner must be an account of the same organization. Restrict, as for knowledge
        // bases: an account that still owns databases cannot be deleted. Its index
        // (OwnerAccountId, OrganizationId) also serves the owner-only list query.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(database => new { database.OwnerAccountId, database.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        // 送出後自動開案 (M7-10, #255): a case type of the same organization, Restrict (types are
        // never deleted, decision O). A null id turns it off; PostgreSQL's MATCH SIMPLE skips the
        // check then.
        builder.HasOne<CaseType>()
            .WithMany()
            .HasForeignKey(database => new { database.AutoCaseTypeId, database.OrganizationId })
            .HasPrincipalKey(type => new { type.Id, type.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
