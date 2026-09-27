using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>Assistants</c>. Organization filter, <c>Organizations</c> foreign key and
/// concurrency token come from <see cref="AppDbContext"/>'s organization scope, like every
/// <c>IOrganizationScoped</c> entity.
/// </summary>
internal sealed class AssistantConfiguration : IEntityTypeConfiguration<Assistant>
{
    public void Configure(EntityTypeBuilder<Assistant> builder)
    {
        builder.ToTable("Assistants");
        builder.HasKey(assistant => assistant.Id);
        builder.Property(assistant => assistant.Id).ValueGeneratedNever();
        builder.Property(assistant => assistant.Name).HasMaxLength(Assistant.NameMaxLength).IsRequired();
        builder.Property(assistant => assistant.Purpose).HasMaxLength(Assistant.PurposeMaxLength).IsRequired();
        builder.Property(assistant => assistant.TemplateId).HasMaxLength(64);
        builder.Property(assistant => assistant.Tone)
            .HasConversion<WireNameConverter<AssistantTone>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(assistant => assistant.RoleInstructions)
            .HasMaxLength(Assistant.RoleInstructionsMaxLength)
            .IsRequired();
        builder.Property(assistant => assistant.KnowledgeScope)
            .HasConversion<WireNameConverter<AssistantKnowledgeScope>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(assistant => assistant.RefusalMessage)
            .HasMaxLength(Assistant.RefusalMessageMaxLength)
            .IsRequired();
        builder.Property(assistant => assistant.Status)
            .HasConversion<WireNameConverter<AssistantStatus>>()
            .HasMaxLength(32)
            .IsRequired();

        // Target of the composite foreign key from the assistant's own rows
        // (AssistantKnowledgeBases), so the database refuses a child row whose organization
        // differs from its assistant's.
        builder.HasAlternateKey(assistant => new { assistant.Id, assistant.OrganizationId });

        // The owner must be an account of the same organization. Restrict: an account that
        // still owns assistants cannot be deleted (ownership transfer is out of M3, plan §8).
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(assistant => new { assistant.OwnerAccountId, assistant.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
