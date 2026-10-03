using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Databases;

/// <summary>Names of <c>DatabaseSubmissions</c> indexes that callers recognize in errors.</summary>
public static class DatabaseSubmissionIndexes
{
    /// <summary>Unique per submitter: a retried or doubly sent submission is one row. The
    /// submission service recognizes a violation of this index by name.</summary>
    public const string IdempotencyKey = "IX_DatabaseSubmissions_SubmittedByAccountId_IdempotencyKey";
}

/// <summary>
/// Table <c>DatabaseSubmissions</c> (M4 #145): the content-free trail of each consented
/// submission. Every foreign key includes <c>OrganizationId</c>, so the database itself refuses a
/// submission whose database, form version or submitter belongs to another organization. All
/// <c>Restrict</c>: a database, version or account with submissions cannot be deleted silently
/// (none of them has a delete endpoint today).
/// </summary>
internal sealed class DatabaseSubmissionConfiguration : IEntityTypeConfiguration<DatabaseSubmission>
{
    private static readonly JsonSerializerOptions TermsJson = new(JsonSerializerDefaults.Web);

    private static readonly ValueComparer<DatabaseConsentTerms> TermsComparer = new(
        (left, right) => Serialize(left) == Serialize(right),
        terms => Serialize(terms).GetHashCode(StringComparison.Ordinal),
        terms => Deserialize(Serialize(terms)));

    public void Configure(EntityTypeBuilder<DatabaseSubmission> builder)
    {
        builder.ToTable("DatabaseSubmissions", table =>
        {
            table.HasCheckConstraint("CK_DatabaseSubmissions_FormVersionNumber", "\"FormVersionNumber\" >= 1");
            table.HasCheckConstraint("CK_DatabaseSubmissions_ConsentTerms", "jsonb_typeof(\"ConsentTerms\") = 'object'");
        });
        builder.HasKey(submission => submission.Id);
        builder.Property(submission => submission.Id).ValueGeneratedNever();
        builder.Property(submission => submission.Source)
            .HasConversion<WireNameConverter<DatabaseSubmissionSource>>()
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(submission => submission.ReceiptNumber)
            .HasMaxLength(DatabaseSubmission.ReceiptNumberMaxLength)
            .IsRequired();
        builder.Property(submission => submission.ConsentTerms)
            .HasConversion(
                new ValueConverter<DatabaseConsentTerms, string>(
                    terms => Serialize(terms),
                    json => Deserialize(json)),
                TermsComparer)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(submission => new { submission.DatabaseId, submission.OrganizationId })
            .HasPrincipalKey(database => new { database.Id, database.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DatabaseFormVersion>()
            .WithMany()
            .HasForeignKey(submission => new { submission.FormVersionId, submission.OrganizationId })
            .HasPrincipalKey(version => new { version.Id, version.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(submission => new { submission.SubmittedByAccountId, submission.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(submission => new { submission.SubmittedByAccountId, submission.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName(DatabaseSubmissionIndexes.IdempotencyKey);
        builder.HasIndex(submission => new { submission.OrganizationId, submission.ReceiptNumber }).IsUnique();

        // A database's records, newest first (#146's timeline, #147's queries).
        builder.HasIndex(submission => new { submission.DatabaseId, submission.SubmittedAt });

        // Target of the entries' composite foreign key.
        builder.HasAlternateKey(submission => new { submission.Id, submission.OrganizationId });
    }

    private static string Serialize(DatabaseConsentTerms? terms) => JsonSerializer.Serialize(terms, TermsJson);

    private static DatabaseConsentTerms Deserialize(string json) =>
        JsonSerializer.Deserialize<DatabaseConsentTerms>(json, TermsJson)
            ?? throw new InvalidOperationException("A submission's consent terms are missing.");
}

/// <summary>
/// Table <c>DatabaseSubmissionEntries</c>: the content of a submission, one row per field.
/// Cascades from its submission; withdrawal (#146) deletes these rows and keeps the submission.
/// </summary>
internal sealed class DatabaseSubmissionEntryConfiguration : IEntityTypeConfiguration<DatabaseSubmissionEntry>
{
    public void Configure(EntityTypeBuilder<DatabaseSubmissionEntry> builder)
    {
        builder.ToTable("DatabaseSubmissionEntries", table =>
            table.HasCheckConstraint("CK_DatabaseSubmissionEntries_Position", "\"Position\" >= 0"));
        builder.HasKey(entry => new { entry.SubmissionId, entry.FieldId });
        builder.Property(entry => entry.FieldId).HasMaxLength(DatabaseFormField.IdMaxLength).IsRequired();
        builder.Property(entry => entry.Label).HasMaxLength(DatabaseFormField.LabelMaxLength).IsRequired();
        builder.Property(entry => entry.FieldType)
            .HasConversion<WireNameConverter<DatabaseFieldType>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(entry => entry.Unit).HasMaxLength(DatabaseFormField.UnitMaxLength).IsRequired();
        builder.Property(entry => entry.Display).IsRequired();
        builder.Property(entry => entry.ChoiceValues).IsRequired();

        builder.HasOne<DatabaseSubmission>()
            .WithMany()
            .HasForeignKey(entry => new { entry.SubmissionId, entry.OrganizationId })
            .HasPrincipalKey(submission => new { submission.Id, submission.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
