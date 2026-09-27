using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChatThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatThreads", x => x.Id);
                    table.UniqueConstraint("AK_ChatThreads_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_ChatThreads_AspNetUsers_AccountId_OrganizationId",
                        columns: x => new { x.AccountId, x.OrganizationId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatThreads_Assistants_AssistantId_OrganizationId",
                        columns: x => new { x.AssistantId, x.OrganizationId },
                        principalTable: "Assistants",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatThreads_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Author = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ReplyKind = table.Column<int>(type: "integer", nullable: true),
                    Notice = table.Column<string>(type: "text", nullable: true),
                    NextSteps = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.UniqueConstraint("AK_ChatMessages_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatThreads_ThreadId_OrganizationId",
                        columns: x => new { x.ThreadId, x.OrganizationId },
                        principalTable: "ChatThreads",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatMessages_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessageCitations",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChunkId = table.Column<Guid>(type: "uuid", nullable: true),
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    KnowledgeBaseName = table.Column<string>(type: "text", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentName = table.Column<string>(type: "text", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    VersionEffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LocationLabel = table.Column<string>(type: "text", nullable: false),
                    Excerpt = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessageCitations", x => new { x.MessageId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_ChatMessageCitations_ChatMessages_MessageId_OrganizationId",
                        columns: x => new { x.MessageId, x.OrganizationId },
                        principalTable: "ChatMessages",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatMessageCitations_KnowledgeBases_KnowledgeBaseId",
                        column: x => x.KnowledgeBaseId,
                        principalTable: "KnowledgeBases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChatMessageCitations_KnowledgeChunks_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "KnowledgeChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChatMessageCitations_KnowledgeDocumentVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "KnowledgeDocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChatMessageCitations_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChatMessageCitations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageCitations_ChunkId",
                table: "ChatMessageCitations",
                column: "ChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageCitations_DocumentId",
                table: "ChatMessageCitations",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageCitations_KnowledgeBaseId",
                table: "ChatMessageCitations",
                column: "KnowledgeBaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageCitations_MessageId_OrganizationId",
                table: "ChatMessageCitations",
                columns: new[] { "MessageId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageCitations_OrganizationId",
                table: "ChatMessageCitations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageCitations_VersionId",
                table: "ChatMessageCitations",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_OrganizationId",
                table: "ChatMessages",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ThreadId_CreatedAt",
                table: "ChatMessages",
                columns: new[] { "ThreadId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ThreadId_OrganizationId",
                table: "ChatMessages",
                columns: new[] { "ThreadId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_AccountId_AssistantId_LastActivityAt",
                table: "ChatThreads",
                columns: new[] { "AccountId", "AssistantId", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_AccountId_LastActivityAt",
                table: "ChatThreads",
                columns: new[] { "AccountId", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_AccountId_OrganizationId",
                table: "ChatThreads",
                columns: new[] { "AccountId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_AssistantId_OrganizationId",
                table: "ChatThreads",
                columns: new[] { "AssistantId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_OrganizationId",
                table: "ChatThreads",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessageCitations");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ChatThreads");
        }
    }
}
