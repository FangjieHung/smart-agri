using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantDrafts</c>. <see cref="AssistantDraft.Payload"/> is stored as
/// PostgreSQL <c>jsonb</c> — opaque to every query except "由草稿建立助理" (#72), which reads it
/// back as text and parses it in <c>AssistantDraftCreationRules</c>.
/// </summary>
internal sealed class AssistantDraftConfiguration : IEntityTypeConfiguration<AssistantDraft>
{
    public void Configure(EntityTypeBuilder<AssistantDraft> builder)
    {
        builder.ToTable("AssistantDrafts");
        builder.HasKey(draft => draft.Id);
        builder.Property(draft => draft.Id).ValueGeneratedNever();
        builder.Property(draft => draft.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(draft => draft.SchemaVersion).IsRequired();
        builder.Property(draft => draft.Revision).IsRequired();

        builder.HasIndex(draft => new { draft.OwnerAccountId, draft.SavedAt });

        // The owner must be an account of the same organization; deleting an account also
        // deletes its drafts (unlike an assistant's owner, which is Restrict — a draft is
        // disposable, an assistant is not).
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(draft => new { draft.OwnerAccountId, draft.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
