using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeBaseShares</c>. Both foreign keys include <c>OrganizationId</c>, so a
/// share can only ever name an account of the knowledge base's own organization, even if
/// application code got it wrong; both cascade, so deleting either side removes the row.
/// </summary>
internal sealed class KnowledgeBaseShareConfiguration : IEntityTypeConfiguration<KnowledgeBaseShare>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseShare> builder)
    {
        builder.ToTable("KnowledgeBaseShares");
        builder.HasKey(share => new { share.KnowledgeBaseId, share.AccountId });

        builder.HasOne<KnowledgeBase>()
            .WithMany()
            .HasForeignKey(share => new { share.KnowledgeBaseId, share.OrganizationId })
            .HasPrincipalKey(knowledgeBase => new { knowledgeBase.Id, knowledgeBase.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(share => new { share.AccountId, share.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
