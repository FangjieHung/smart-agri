using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DocumentId",
                table: "KnowledgeActivities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VersionId",
                table: "KnowledgeActivities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                    table.UniqueConstraint("AK_KnowledgeDocuments_Id_KnowledgeBaseId_OrganizationId", x => new { x.Id, x.KnowledgeBaseId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_KnowledgeBases_KnowledgeBaseId_Organizat~",
                        columns: x => new { x.KnowledgeBaseId, x.OrganizationId },
                        principalTable: "KnowledgeBases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeDocumentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ProcessingStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Issue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UploadBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocumentVersions", x => x.Id);
                    table.UniqueConstraint("AK_KnowledgeDocumentVersions_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_KnowledgeDocumentVersions_AspNetUsers_UploadedByAccountId_O~",
                        columns: x => new { x.UploadedByAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocumentVersions_KnowledgeDocuments_DocumentId_Kno~",
                        columns: x => new { x.DocumentId, x.KnowledgeBaseId, x.OrganizationId },
                        principalTable: "KnowledgeDocuments",
                        principalColumns: new[] { "Id", "KnowledgeBaseId", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocumentVersions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeFileContents",
                columns: table => new
                {
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeFileContents", x => x.VersionId);
                    table.ForeignKey(
                        name: "FK_KnowledgeFileContents_KnowledgeDocumentVersions_VersionId_O~",
                        columns: x => new { x.VersionId, x.OrganizationId },
                        principalTable: "KnowledgeDocumentVersions",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeFileContents_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_KnowledgeBaseId_Name",
                table: "KnowledgeDocuments",
                columns: new[] { "KnowledgeBaseId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_KnowledgeBaseId_OrganizationId",
                table: "KnowledgeDocuments",
                columns: new[] { "KnowledgeBaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_OrganizationId",
                table: "KnowledgeDocuments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentVersions_DocumentId_KnowledgeBaseId_Organi~",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "DocumentId", "KnowledgeBaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentVersions_DocumentId_VersionNumber",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "DocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentVersions_KnowledgeBaseId_Sha256",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "KnowledgeBaseId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentVersions_OrganizationId",
                table: "KnowledgeDocumentVersions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentVersions_UploadedByAccountId_OrganizationId",
                table: "KnowledgeDocumentVersions",
                columns: new[] { "UploadedByAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeFileContents_OrganizationId",
                table: "KnowledgeFileContents",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeFileContents_VersionId_OrganizationId",
                table: "KnowledgeFileContents",
                columns: new[] { "VersionId", "OrganizationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeFileContents");

            migrationBuilder.DropTable(
                name: "KnowledgeDocumentVersions");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                table: "KnowledgeActivities");

            migrationBuilder.DropColumn(
                name: "VersionId",
                table: "KnowledgeActivities");
        }
    }
}
