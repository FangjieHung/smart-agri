using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantTestResults</c> (M3.5 plan §4, issue #124): cascades with its run.
/// <see cref="AssistantTestResult.TestCaseId"/> is not a foreign key — a result is a snapshot
/// that outlives its test case. Id lists are <c>jsonb</c>, as in
/// <c>AssistantTestCaseConfiguration</c>.
/// </summary>
internal sealed class AssistantTestResultConfiguration : IEntityTypeConfiguration<AssistantTestResult>
{
    private static readonly ValueComparer<IReadOnlyList<Guid>> GuidListComparer = new(
        (left, right) => Enumerable.SequenceEqual(left ?? new List<Guid>(), right ?? new List<Guid>()),
        list => list.Aggregate(0, (hash, value) => HashCode.Combine(hash, value)),
        list => list.ToList());

    private static readonly ValueConverter<IReadOnlyList<Guid>, string> GuidListConverter = new(
        list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null) ?? new List<Guid>());

    public void Configure(EntityTypeBuilder<AssistantTestResult> builder)
    {
        builder.ToTable("AssistantTestResults", table => table.HasCheckConstraint(
            "CK_AssistantTestResults_Passed",
            "\"Passed\" = (\"FailureReason\" IS NULL)"));
        builder.HasKey(result => result.Id);
        builder.Property(result => result.Id).ValueGeneratedNever();
        builder.Property(result => result.QuestionSnapshot).HasMaxLength(AssistantTestCase.QuestionMaxLength).IsRequired();
        builder.Property(result => result.ExpectedKind)
            .HasConversion<WireNameConverter<AssistantTestExpectedKind>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(result => result.ExpectedDocumentIds)
            .HasConversion(GuidListConverter, GuidListComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(result => result.ActualKind)
            .HasConversion<WireNameConverter<AnswerReplyKind>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(result => result.AnswerText).IsRequired();
        builder.Property(result => result.CitedDocumentIds)
            .HasConversion(GuidListConverter, GuidListComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(result => result.RejectionReason)
            .HasConversion(
                new ValueConverter<AnswerRejectionReason?, string?>(
                    reason => reason.HasValue ? WireNames<AnswerRejectionReason>.ToWire(reason.Value) : null,
                    name => name == null ? (AnswerRejectionReason?)null : WireNames<AnswerRejectionReason>.Parse(name)))
            .HasMaxLength(32);
        builder.Property(result => result.FailureReason)
            .HasConversion(
                new ValueConverter<AssistantTestFailureReason?, string?>(
                    reason => reason.HasValue ? WireNames<AssistantTestFailureReason>.ToWire(reason.Value) : null,
                    name => name == null ? (AssistantTestFailureReason?)null : WireNames<AssistantTestFailureReason>.Parse(name)))
            .HasMaxLength(32);

        builder.HasIndex(result => new { result.RunId, result.Ordinal });

        builder.HasOne<AssistantTestRun>()
            .WithMany()
            .HasForeignKey(result => new { result.RunId, result.OrganizationId })
            .HasPrincipalKey(run => new { run.Id, run.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
