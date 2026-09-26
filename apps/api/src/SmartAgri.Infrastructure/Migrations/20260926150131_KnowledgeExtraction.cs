using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_KnowledgeDocumentVersions_Id_DocumentId_KnowledgeBaseId_Org~",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "Id", "DocumentId", "KnowledgeBaseId", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "KnowledgeExtractedUnits",
                columns: table => new
                {
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LocationLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Readable = table.Column<bool>(type: "boolean", nullable: false),
                    IssueCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeExtractedUnits", x => new { x.VersionId, x.Ordinal });
                    table.UniqueConstraint("AK_KnowledgeExtractedUnits_VersionId_Ordinal_OrganizationId", x => new { x.VersionId, x.Ordinal, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_KnowledgeExtractedUnits_KnowledgeDocumentVersions_VersionId~",
                        columns: x => new { x.VersionId, x.OrganizationId },
                        principalTable: "KnowledgeDocumentVersions",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeExtractedUnits_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitOrdinal = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    LocationLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Excluded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_KnowledgeDocumentVersions_VersionId_Documen~",
                        columns: x => new { x.VersionId, x.DocumentId, x.KnowledgeBaseId, x.OrganizationId },
                        principalTable: "KnowledgeDocumentVersions",
                        principalColumns: new[] { "Id", "DocumentId", "KnowledgeBaseId", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_KnowledgeExtractedUnits_VersionId_UnitOrdin~",
                        columns: x => new { x.VersionId, x.UnitOrdinal, x.OrganizationId },
                        principalTable: "KnowledgeExtractedUnits",
                        principalColumns: new[] { "VersionId", "Ordinal", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_OrganizationId",
                table: "KnowledgeChunks",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_VersionId_DocumentId_KnowledgeBaseId_Organi~",
                table: "KnowledgeChunks",
                columns: new[] { "VersionId", "DocumentId", "KnowledgeBaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_VersionId_UnitOrdinal_Ordinal",
                table: "KnowledgeChunks",
                columns: new[] { "VersionId", "UnitOrdinal", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_VersionId_UnitOrdinal_OrganizationId",
                table: "KnowledgeChunks",
                columns: new[] { "VersionId", "UnitOrdinal", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeExtractedUnits_OrganizationId",
                table: "KnowledgeExtractedUnits",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeExtractedUnits_VersionId_OrganizationId",
                table: "KnowledgeExtractedUnits",
                columns: new[] { "VersionId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeChunks");

            migrationBuilder.DropTable(
                name: "KnowledgeExtractedUnits");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_KnowledgeDocumentVersions_Id_DocumentId_KnowledgeBaseId_Org~",
                table: "KnowledgeDocumentVersions");
        }
    }
}
