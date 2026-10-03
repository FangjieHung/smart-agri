using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantDatabases</c> (M4 #148). Both foreign keys include <c>OrganizationId</c>, so a
/// connection can only ever name a database of the assistant's own organization, even if
/// application code got it wrong; both cascade, like <see cref="AssistantKnowledgeBaseConfiguration"/> (the foreign keys' own
/// indexes also serve "which assistants are connected to this database").
/// At most one row per assistant collects forms (partial unique index), and that row always has a
/// purpose (check constraint).
/// </summary>
internal sealed class AssistantDatabaseConfiguration : IEntityTypeConfiguration<AssistantDatabase>
{
    public void Configure(EntityTypeBuilder<AssistantDatabase> builder)
    {
        builder.ToTable("AssistantDatabases", table => table.HasCheckConstraint(
            "CK_AssistantDatabases_CollectionPurpose",
            "(\"CollectsForms\" AND length(btrim(\"CollectionPurpose\")) > 0) OR (NOT \"CollectsForms\" AND \"CollectionPurpose\" = '')"));
        builder.HasKey(link => new { link.AssistantId, link.DatabaseId });
        builder.Property(link => link.CollectionPurpose)
            .HasMaxLength(AssistantDatabase.CollectionPurposeMaxLength)
            .IsRequired();

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(link => new { link.AssistantId, link.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(link => new { link.DatabaseId, link.OrganizationId })
            .HasPrincipalKey(database => new { database.Id, database.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // One form target per assistant.
        builder.HasIndex(link => link.AssistantId)
            .IsUnique()
            .HasFilter("\"CollectsForms\"")
            .HasDatabaseName("IX_AssistantDatabases_AssistantId_CollectsForms");
    }
}
