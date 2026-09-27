using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantKnowledgeBases</c>. Both foreign keys include <c>OrganizationId</c>, so a
/// connection can only ever name a knowledge base of the assistant's own organization, even if
/// application code got it wrong; both cascade, so deleting either side removes the row (M3
/// plan §4: "知識庫刪除時連帶刪除連接").
/// </summary>
internal sealed class AssistantKnowledgeBaseConfiguration : IEntityTypeConfiguration<AssistantKnowledgeBase>
{
    public void Configure(EntityTypeBuilder<AssistantKnowledgeBase> builder)
    {
        builder.ToTable("AssistantKnowledgeBases");
        builder.HasKey(link => new { link.AssistantId, link.KnowledgeBaseId });

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(link => new { link.AssistantId, link.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<KnowledgeBase>()
            .WithMany()
            .HasForeignKey(link => new { link.KnowledgeBaseId, link.OrganizationId })
            .HasPrincipalKey(knowledgeBase => new { knowledgeBase.Id, knowledgeBase.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
