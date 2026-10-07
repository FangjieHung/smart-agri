using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain;
using SmartAgri.Domain.Answers;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Answers;

/// <summary>
/// Table <c>AnswerOutcomes</c>: the answer-pipeline result log (M3.5 plan §3, §4, Slice 6). No
/// column holds content (a model test asserts <see cref="AnswerOutcome"/> has no <see cref="string"/>
/// property at all). No assistant table foreign key: <see cref="AnswerOutcome.AssistantId"/> is
/// <see langword="null"/> for a draft's trial answer, and rows must outlive a deleted assistant
/// (an operational log, not a cascading child of it).
/// </summary>
internal sealed class AnswerOutcomeConfiguration : IEntityTypeConfiguration<AnswerOutcome>
{
    private static readonly ValueComparer<IReadOnlyList<Guid>> CitedDocumentIdsComparer = new(
        (left, right) => Enumerable.SequenceEqual(left ?? new List<Guid>(), right ?? new List<Guid>()),
        list => list.Aggregate(0, (hash, value) => HashCode.Combine(hash, value)),
        list => list.ToList());

    public void Configure(EntityTypeBuilder<AnswerOutcome> builder)
    {
        builder.ToTable("AnswerOutcomes");
        builder.HasKey(outcome => outcome.Id);
        builder.Property(outcome => outcome.Id).ValueGeneratedNever();

        builder.Property(outcome => outcome.Channel)
            .HasConversion<WireNameConverter<AnswerOutcomeChannel>>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(outcome => outcome.ReplyKind)
            .HasConversion<WireNameConverter<AnswerReplyKind>>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(outcome => outcome.RejectionReason)
            .HasConversion(
                new ValueConverter<AnswerRejectionReason?, string?>(
                    reason => reason.HasValue ? WireNames<AnswerRejectionReason>.ToWire(reason.Value) : null,
                    name => name == null ? (AnswerRejectionReason?)null : WireNames<AnswerRejectionReason>.Parse(name)))
            .HasMaxLength(32);

        // M4 #178: the result category of a database-query row; null for every other kind.
        builder.Property(outcome => outcome.DatabaseQueryResult)
            .HasConversion(
                new ValueConverter<AnswerDatabaseQueryResult?, string?>(
                    result => result.HasValue ? WireNames<AnswerDatabaseQueryResult>.ToWire(result.Value) : null,
                    name => name == null ? (AnswerDatabaseQueryResult?)null : WireNames<AnswerDatabaseQueryResult>.Parse(name)))
            .HasMaxLength(32);

        // #302: whether candidate passages below the relevance threshold were used. Existing rows
        // predate candidates: the migration fills them with false.
        builder.Property(outcome => outcome.UsedCandidates).IsRequired();

        builder.Property(outcome => outcome.CitedDocumentIds)
            .HasConversion(
                new ValueConverter<IReadOnlyList<Guid>, string>(
                    list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null) ?? new List<Guid>()),
                CitedDocumentIdsComparer)
            .HasColumnType("jsonb")
            .IsRequired();

        // An organization's outcomes over time, by assistant (analytics, Slice 6) and overall
        // (operations summary).
        builder.HasIndex(outcome => new { outcome.OrganizationId, outcome.AssistantId, outcome.At });
        builder.HasIndex(outcome => new { outcome.OrganizationId, outcome.At });
    }
}
