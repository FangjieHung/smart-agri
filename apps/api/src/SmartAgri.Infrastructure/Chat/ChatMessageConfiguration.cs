using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Application.Chat;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Infrastructure.Chat;

/// <summary>
/// Table <c>ChatMessages</c>. One composite foreign key to its thread (cascades — deleting a
/// thread deletes its messages, which deletes their citations in turn).
/// <see cref="ChatMessage.NextSteps"/> is stored as <c>jsonb</c> text (a short, fixed list of
/// strings; never queried by content, only ever read back whole — mirrors
/// <c>KnowledgeActivityConfiguration.Detail</c>).
/// </summary>
internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    private static readonly ValueComparer<IReadOnlyList<string>> NextStepsComparer = new(
        (left, right) => Enumerable.SequenceEqual(left ?? new List<string>(), right ?? new List<string>()),
        list => list.Aggregate(0, (hash, value) => HashCode.Combine(hash, value)),
        list => list.ToList());

    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("ChatMessages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.Text).IsRequired();
        // Only ever set for an Account turn (ticket #105); not returned by any API view.
        builder.Property(message => message.ClientMessageId).HasMaxLength(ChatRunRules.ClientMessageIdMaxLength);
        builder.Property(message => message.NextSteps)
            .HasConversion(
                new ValueConverter<IReadOnlyList<string>, string>(
                    list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<List<string>>(json, (JsonSerializerOptions?)null) ?? new List<string>()),
                NextStepsComparer)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne<ChatThread>()
            .WithMany()
            .HasForeignKey(message => new { message.ThreadId, message.OrganizationId })
            .HasPrincipalKey(thread => new { thread.Id, thread.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // A thread's messages, oldest first (GET .../chat).
        builder.HasIndex(message => new { message.ThreadId, message.Sequence }).IsUnique();
    }
}
