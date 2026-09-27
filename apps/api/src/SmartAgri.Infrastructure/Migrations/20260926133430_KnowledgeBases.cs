using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeBases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnowledgeActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeActivities_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeBases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SharingScope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AllowOriginalDownload = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBases", x => x.Id);
                    table.UniqueConstraint("AK_KnowledgeBases_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_KnowledgeBases_AspNetUsers_OwnerAccountId_OrganizationId",
                        columns: x => new { x.OwnerAccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeBases_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeBaseShares",
                columns: table => new
                {
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBaseShares", x => new { x.KnowledgeBaseId, x.AccountId });
                    table.ForeignKey(
                        name: "FK_KnowledgeBaseShares_AspNetUsers_AccountId_OrganizationId",
                        columns: x => new { x.AccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeBaseShares_KnowledgeBases_KnowledgeBaseId_Organiza~",
                        columns: x => new { x.KnowledgeBaseId, x.OrganizationId },
                        principalTable: "KnowledgeBases",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeBaseShares_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeActivities_OrganizationId_KnowledgeBaseId_At",
                table: "KnowledgeActivities",
                columns: new[] { "OrganizationId", "KnowledgeBaseId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBases_OrganizationId",
                table: "KnowledgeBases",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBases_OwnerAccountId_OrganizationId",
                table: "KnowledgeBases",
                columns: new[] { "OwnerAccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseShares_AccountId_OrganizationId",
                table: "KnowledgeBaseShares",
                columns: new[] { "AccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseShares_KnowledgeBaseId_OrganizationId",
                table: "KnowledgeBaseShares",
                columns: new[] { "KnowledgeBaseId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBaseShares_OrganizationId",
                table: "KnowledgeBaseShares",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeActivities");

            migrationBuilder.DropTable(
                name: "KnowledgeBaseShares");

            migrationBuilder.DropTable(
                name: "KnowledgeBases");
        }
    }
}
