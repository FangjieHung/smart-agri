using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantShares</c>. Both foreign keys include <c>OrganizationId</c>, so a share
/// can only ever name an account of the assistant's own organization, even if application code
/// got it wrong; both cascade, so deleting either side removes the row (mirrors
/// <c>KnowledgeBaseShareConfiguration</c>).
/// </summary>
internal sealed class AssistantShareConfiguration : IEntityTypeConfiguration<AssistantShare>
{
    public void Configure(EntityTypeBuilder<AssistantShare> builder)
    {
        builder.ToTable("AssistantShares");
        builder.HasKey(share => new { share.AssistantId, share.AccountId });

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(share => new { share.AssistantId, share.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(share => new { share.AccountId, share.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
