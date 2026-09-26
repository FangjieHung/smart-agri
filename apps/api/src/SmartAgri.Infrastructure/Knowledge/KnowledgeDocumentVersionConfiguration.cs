using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeDocumentVersions</c>. Belongs to its document through a composite
/// foreign key (document, knowledge base, organization) that cascades; its file is a
/// separate table (<see cref="KnowledgeFileContentConfiguration"/>).
/// </summary>
/// <remarks>
/// Review (Slice 8): <c>ReviewState</c> is stored by wire name and is a concurrency token; a
/// check constraint holds the approval columns to it (pending: none set; approved: approver,
/// time and an effective time not before the approval, and only for a readable processed
/// version), and a partial index on the approved versions serves "the current effective
/// version of each document" (<c>RetrievableChunks.CurrentEffectiveVersion</c>'s
/// <c>NOT EXISTS</c> over the document's approved versions by effective time).
/// </remarks>
internal sealed class KnowledgeDocumentVersionConfiguration : IEntityTypeConfiguration<KnowledgeDocumentVersion>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentVersion> builder)
    {
        builder.ToTable("KnowledgeDocumentVersions");
        builder.HasKey(version => version.Id);
        builder.Property(version => version.Id).ValueGeneratedNever();
        builder.Property(version => version.FileName).HasMaxLength(KnowledgeDocumentVersion.FileNameMaxLength).IsRequired();
        builder.Property(version => version.ContentType).HasMaxLength(KnowledgeDocumentVersion.ContentTypeMaxLength).IsRequired();
        builder.Property(version => version.Sha256).HasMaxLength(KnowledgeDocumentVersion.Sha256Length).IsFixedLength().IsRequired();
        builder.Property(version => version.Issue).HasMaxLength(KnowledgeDocumentVersion.IssueMaxLength);

        // Status changes are compare-and-set: saved only if the row still has the status it
        // was read with (see KnowledgeDocumentVersion's remarks).
        builder.Property(version => version.ProcessingStatus)
            .HasConversion<WireNameConverter<KnowledgeDocumentStatus>>()
            .HasMaxLength(32)
            .IsRequired()
            .IsConcurrencyToken();

        builder.Property(version => version.ReviewState)
            .HasConversion<WireNameConverter<KnowledgeReviewState>>()
            .HasMaxLength(32)
            .IsRequired()
            .IsConcurrencyToken();
        builder.ToTable(table => table.HasCheckConstraint("CK_KnowledgeDocumentVersions_Review", ReviewCheck()));

        // Target of the file contents' and extracted units' composite foreign keys.
        builder.HasAlternateKey(version => new { version.Id, version.OrganizationId });

        // Target of the chunks' composite foreign key: a chunk's DocumentId and
        // KnowledgeBaseId (kept for filtering retrieval) must be its version's.
        builder.HasAlternateKey(version => new { version.Id, version.DocumentId, version.KnowledgeBaseId, version.OrganizationId });

        // Navigable both ways for query expressions only (RetrievableChunks): nothing loads
        // either side unless a query includes it.
        builder.HasOne(version => version.Document)
            .WithMany(document => document.Versions)
            .HasForeignKey(version => new { version.DocumentId, version.KnowledgeBaseId, version.OrganizationId })
            .HasPrincipalKey(document => new { document.Id, document.KnowledgeBaseId, document.OrganizationId })
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // The uploader and the approver are accounts of the same organization. Restrict, like
        // a knowledge base's owner: accounts are not deleted in M2.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(version => new { version.UploadedByAccountId, version.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(version => new { version.ApprovedByAccountId, version.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(version => new { version.DocumentId, version.VersionNumber }).IsUnique();

        // The duplicate-content rule: one SHA-256 per knowledge base, whichever document or
        // version has it. Enforced here so concurrent uploads cannot both succeed.
        builder.HasIndex(version => new { version.KnowledgeBaseId, version.Sha256 }).IsUnique();

        // Each document's approved versions by effective time: the version in effect is the
        // latest one not after now, found without reading pending versions.
        builder.HasIndex(version => new { version.DocumentId, version.EffectiveFrom, version.VersionNumber })
            .HasFilter($"\"ReviewState\" = '{Wire(KnowledgeReviewState.Approved)}'");
    }

    /// <summary>Pending: no approval columns. Approved: all of them, effective no earlier than
    /// approved, and only a readable processed version (retry and processing never touch
    /// those, so an approved version stays servable).</summary>
    private static string ReviewCheck() =>
        $"(\"ReviewState\" = '{Wire(KnowledgeReviewState.PendingReview)}' AND \"EffectiveFrom\" IS NULL " +
        "AND \"ApprovedByAccountId\" IS NULL AND \"ApprovedAt\" IS NULL) OR " +
        $"(\"ReviewState\" = '{Wire(KnowledgeReviewState.Approved)}' AND \"EffectiveFrom\" IS NOT NULL " +
        "AND \"ApprovedByAccountId\" IS NOT NULL AND \"ApprovedAt\" IS NOT NULL AND \"EffectiveFrom\" >= \"ApprovedAt\" " +
        $"AND \"ProcessingStatus\" IN ('{WireNames<KnowledgeDocumentStatus>.ToWire(KnowledgeDocumentStatus.Ready)}', " +
        $"'{WireNames<KnowledgeDocumentStatus>.ToWire(KnowledgeDocumentStatus.PartiallyReadable)}'))";

    private static string Wire(KnowledgeReviewState state) => WireNames<KnowledgeReviewState>.ToWire(state);
}
