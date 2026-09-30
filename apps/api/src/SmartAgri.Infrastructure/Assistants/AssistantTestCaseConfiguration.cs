using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantTestCases</c>. <see cref="AssistantTestCase.ExpectedDocumentIds"/> is stored
/// as <c>jsonb</c> (a short list of guids; never queried by content — mirrors
/// <c>ChatMessageConfiguration.NextSteps</c>). Cascades with its assistant, like
/// <c>AssistantKnowledgeBaseConfiguration</c>.
/// </summary>
internal sealed class AssistantTestCaseConfiguration : IEntityTypeConfiguration<AssistantTestCase>
{
    private static readonly ValueComparer<IReadOnlyList<Guid>> ExpectedDocumentIdsComparer = new(
        (left, right) => Enumerable.SequenceEqual(left ?? new List<Guid>(), right ?? new List<Guid>()),
        list => list.Aggregate(0, (hash, value) => HashCode.Combine(hash, value)),
        list => list.ToList());

    public void Configure(EntityTypeBuilder<AssistantTestCase> builder)
    {
        builder.ToTable("AssistantTestCases");
        builder.HasKey(testCase => testCase.Id);
        builder.Property(testCase => testCase.Id).ValueGeneratedNever();
        builder.Property(testCase => testCase.Question).HasMaxLength(AssistantTestCase.QuestionMaxLength).IsRequired();
        builder.Property(testCase => testCase.Category)
            .HasConversion<WireNameConverter<AssistantTestCaseCategory>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(testCase => testCase.ExpectedKind)
            .HasConversion<WireNameConverter<AssistantTestExpectedKind>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(testCase => testCase.ExpectedDocumentIds)
            .HasConversion(
                new ValueConverter<IReadOnlyList<Guid>, string>(
                    list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null) ?? new List<Guid>()),
                ExpectedDocumentIdsComparer)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasIndex(testCase => new { testCase.AssistantId, testCase.Ordinal });

        // Deleting an assistant deletes its test cases (M3.5 plan §4).
        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(testCase => new { testCase.AssistantId, testCase.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // FollowUpOfId is validated at the application layer (must be an earlier test case of the
        // same assistant) but is not a database foreign key: it is purely informational, and a
        // dangling reference left behind by deleting the target case is harmless.
    }
}
