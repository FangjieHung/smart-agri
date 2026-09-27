using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VersionReviewAndDisable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "KnowledgeDocumentVersions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedByAccountId",
                table: "KnowledgeDocumentVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EffectiveFrom",
                table: "KnowledgeDocumentVersions",
                type: "timestamp with time zone",
                nullable: true);

            // Every existing version is pending review: nothing uploaded before this slice was
            // ever approved, and every version, version 1 included, needs a person's approval
            // (M2 plan §7 decision 4). The column keeps this default; the app always writes it.
            migrationBuilder.AddColumn<string>(
                name: "ReviewState",
                table: "KnowledgeDocumentVersions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "pending-review");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisabledAt",
                table: "KnowledgeDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DisabledByAccountId",
                table: "KnowledgeDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabledReason",
                table: "KnowledgeDocuments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentVersions_ApprovedByAccountId_OrganizationId",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "ApprovedByAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentVersions_DocumentId_EffectiveFrom_VersionN~",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "DocumentId", "EffectiveFrom", "VersionNumber" },
                filter: "\"ReviewState\" = 'approved'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeDocumentVersions_Review",
                table: "KnowledgeDocumentVersions",
                sql: "(\"ReviewState\" = 'pending-review' AND \"EffectiveFrom\" IS NULL AND \"ApprovedByAccountId\" IS NULL AND \"ApprovedAt\" IS NULL) OR (\"ReviewState\" = 'approved' AND \"EffectiveFrom\" IS NOT NULL AND \"ApprovedByAccountId\" IS NOT NULL AND \"ApprovedAt\" IS NOT NULL AND \"EffectiveFrom\" >= \"ApprovedAt\" AND \"ProcessingStatus\" IN ('ready', 'partially-readable'))");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_DisabledByAccountId_OrganizationId",
                table: "KnowledgeDocuments",
                columns: new[] { "DisabledByAccountId", "OrganizationId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeDocuments_Disabled",
                table: "KnowledgeDocuments",
                sql: "(\"DisabledAt\" IS NULL AND \"DisabledByAccountId\" IS NULL AND \"DisabledReason\" IS NULL) OR (\"DisabledAt\" IS NOT NULL AND \"DisabledByAccountId\" IS NOT NULL AND \"DisabledReason\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_KnowledgeDocuments_AspNetUsers_DisabledByAccountId_Organiza~",
                table: "KnowledgeDocuments",
                columns: new[] { "DisabledByAccountId", "OrganizationId" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_KnowledgeDocumentVersions_AspNetUsers_ApprovedByAccountId_O~",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "ApprovedByAccountId", "OrganizationId" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KnowledgeDocuments_AspNetUsers_DisabledByAccountId_Organiza~",
                table: "KnowledgeDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_KnowledgeDocumentVersions_AspNetUsers_ApprovedByAccountId_O~",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocumentVersions_ApprovedByAccountId_OrganizationId",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocumentVersions_DocumentId_EffectiveFrom_VersionN~",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeDocumentVersions_Review",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocuments_DisabledByAccountId_OrganizationId",
                table: "KnowledgeDocuments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeDocuments_Disabled",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropColumn(
                name: "ApprovedByAccountId",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropColumn(
                name: "ReviewState",
                table: "KnowledgeDocumentVersions");

            migrationBuilder.DropColumn(
                name: "DisabledAt",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "DisabledByAccountId",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "DisabledReason",
                table: "KnowledgeDocuments");
        }
    }
}
