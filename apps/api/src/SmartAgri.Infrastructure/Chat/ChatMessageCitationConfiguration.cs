using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Chat;

/// <summary>
/// Table <c>ChatMessageCitations</c>. The foreign key to its message cascades (an
/// organization-scoped composite, like every other message/thread link here). The four
/// back-references to the source (<see cref="ChatMessageCitation.ChunkId"/>,
/// <see cref="ChatMessageCitation.KnowledgeBaseId"/>, <see cref="ChatMessageCitation.DocumentId"/>,
/// <see cref="ChatMessageCitation.VersionId"/>) are deliberately single-column, principal-<c>Id</c>-only
/// foreign keys with <c>SET NULL</c>: a composite <c>(Id, OrganizationId)</c> key here would null
/// out this row's own <see cref="ChatMessageCitation.OrganizationId"/> on delete (breaking
/// tenancy), which citations must never do — the snapshot columns
/// (<see cref="ChatMessageCitation.KnowledgeBaseName"/> etc.) are what a citation display always
/// reads; these four ids are optional "is this still findable" back-references only (M3 plan §4:
/// "外鍵在來源刪除時設成 null").
/// </summary>
internal sealed class ChatMessageCitationConfiguration : IEntityTypeConfiguration<ChatMessageCitation>
{
    public void Configure(EntityTypeBuilder<ChatMessageCitation> builder)
    {
        builder.ToTable("ChatMessageCitations");
        builder.HasKey(citation => new { citation.MessageId, citation.Ordinal });
        builder.Property(citation => citation.KnowledgeBaseName).IsRequired();
        builder.Property(citation => citation.DocumentName).IsRequired();
        builder.Property(citation => citation.LocationLabel).IsRequired();
        builder.Property(citation => citation.Excerpt).IsRequired();
        builder.Property(citation => citation.Text).IsRequired();

        builder.HasOne<ChatMessage>()
            .WithMany()
            .HasForeignKey(citation => new { citation.MessageId, citation.OrganizationId })
            .HasPrincipalKey(message => new { message.Id, message.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<KnowledgeChunk>()
            .WithMany()
            .HasForeignKey(citation => citation.ChunkId)
            .HasPrincipalKey(chunk => chunk.Id)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<KnowledgeBase>()
            .WithMany()
            .HasForeignKey(citation => citation.KnowledgeBaseId)
            .HasPrincipalKey(knowledgeBase => knowledgeBase.Id)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<KnowledgeDocument>()
            .WithMany()
            .HasForeignKey(citation => citation.DocumentId)
            .HasPrincipalKey(document => document.Id)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<KnowledgeDocumentVersion>()
            .WithMany()
            .HasForeignKey(citation => citation.VersionId)
            .HasPrincipalKey(version => version.Id)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
