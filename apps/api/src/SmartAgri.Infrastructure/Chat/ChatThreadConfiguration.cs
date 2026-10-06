using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Infrastructure.Chat;

/// <summary>
/// Table <c>ChatThreads</c>. Both foreign keys include <c>OrganizationId</c>; both cascade —
/// deleting the assistant deletes every account's threads with it (M3 plan §7 decision G),
/// deleting the account deletes its own threads.
/// </summary>
internal sealed class ChatThreadConfiguration : IEntityTypeConfiguration<ChatThread>
{
    public void Configure(EntityTypeBuilder<ChatThread> builder)
    {
        builder.ToTable("ChatThreads");
        builder.HasKey(thread => thread.Id);
        builder.Property(thread => thread.Id).ValueGeneratedNever();
        builder.Property(thread => thread.Title).HasMaxLength(ChatThread.TitleMaxLength).IsRequired();

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(thread => new { thread.AssistantId, thread.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(thread => new { thread.AccountId, thread.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // The thread list, always filtered by (account, assistant) and sorted by recency
        // (mapping §5.4, §2.4); also backs the cross-assistant "recent 10" sidebar query.
        builder.HasIndex(thread => new { thread.AccountId, thread.AssistantId, thread.LastActivityAt });
        builder.HasIndex(thread => new { thread.AccountId, thread.LastActivityAt });

        // The daily retention cleanup and its preview: an organization's threads whose last message
        // is before a cutoff (M6 plan §3 G).
        builder.HasIndex(thread => new { thread.OrganizationId, thread.LastActivityAt });
    }
}
