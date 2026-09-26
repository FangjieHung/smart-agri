using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeBases</c>. Organization filter, <c>Organizations</c> foreign key and
/// concurrency token come from <see cref="AppDbContext"/>'s organization scope, like every
/// <c>IOrganizationScoped</c> entity.
/// </summary>
internal sealed class KnowledgeBaseConfiguration : IEntityTypeConfiguration<KnowledgeBase>
{
    public void Configure(EntityTypeBuilder<KnowledgeBase> builder)
    {
        builder.ToTable("KnowledgeBases");
        builder.HasKey(knowledgeBase => knowledgeBase.Id);
        builder.Property(knowledgeBase => knowledgeBase.Id).ValueGeneratedNever();
        builder.Property(knowledgeBase => knowledgeBase.Name).HasMaxLength(KnowledgeBase.NameMaxLength).IsRequired();
        builder.Property(knowledgeBase => knowledgeBase.Purpose).HasMaxLength(KnowledgeBase.PurposeMaxLength).IsRequired();
        builder.Property(knowledgeBase => knowledgeBase.SharingScope)
            .HasConversion<WireNameConverter<KnowledgeSharingScope>>()
            .HasMaxLength(32)
            .IsRequired();

        // Target of the composite foreign keys from the knowledge base's own rows
        // (KnowledgeBaseShares, KnowledgeDocuments), so the database refuses a child row
        // whose organization differs from its knowledge base's.
        builder.HasAlternateKey(knowledgeBase => new { knowledgeBase.Id, knowledgeBase.OrganizationId });

        // The owner must be an account of the same organization. Restrict: an account that
        // still owns knowledge bases cannot be deleted (ownership transfer is out of M2).
        // Its index (OwnerAccountId, OrganizationId) also serves the owner-only list query.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(knowledgeBase => new { knowledgeBase.OwnerAccountId, knowledgeBase.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
