using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorCommandActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoordinatorCommandActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Step = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StepsText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ImageUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RecipientLineUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UndoesActionId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExternalRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedByLineUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoordinatorCommandActions", x => x.Id);
                    table.UniqueConstraint("AK_CoordinatorCommandActions_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_CoordinatorCommandActions_Completed", "\"Status\" NOT IN ('succeeded', 'failed', 'done-manually') OR \"CompletedAt\" IS NOT NULL");
                    table.CheckConstraint("CK_CoordinatorCommandActions_Done", "(\"Status\" = 'done-manually') = (\"CompletedByLineUserId\" IS NOT NULL)");
                    table.CheckConstraint("CK_CoordinatorCommandActions_Failure", "\"Status\" <> 'failed' OR \"FailureCode\" IS NOT NULL");
                    table.CheckConstraint("CK_CoordinatorCommandActions_Ordering", "(\"Kind\" = 'ordering') = (\"Step\" IN ('pause', 'resume')) AND (\"Kind\" = 'ordering') = (\"Name\" IS NOT NULL) AND (\"Kind\" <> 'ordering' OR \"Mode\" = 'reminder')");
                    table.CheckConstraint("CK_CoordinatorCommandActions_Position", "\"Position\" >= 0 AND \"Attempts\" >= 0");
                    table.CheckConstraint("CK_CoordinatorCommandActions_Undo", "(\"Step\" <> 'undo' OR \"UndoesActionId\" IS NOT NULL) AND (\"Step\" NOT IN ('apply', 'pause') OR \"UndoesActionId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CoordinatorCommandActions_CoordinatorCommandActions_UndoesA~",
                        columns: x => new { x.UndoesActionId, x.OrganizationId },
                        principalTable: "CoordinatorCommandActions",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoordinatorCommandActions_CoordinatorCommands_CommandId_Org~",
                        columns: x => new { x.CommandId, x.OrganizationId },
                        principalTable: "CoordinatorCommands",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoordinatorCommandActions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommandActions_CommandId_OrganizationId",
                table: "CoordinatorCommandActions",
                columns: new[] { "CommandId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommandActions_CommandId_Position",
                table: "CoordinatorCommandActions",
                columns: new[] { "CommandId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommandActions_OrganizationId",
                table: "CoordinatorCommandActions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_CoordinatorCommandActions_UndoesActionId_OrganizationId",
                table: "CoordinatorCommandActions",
                columns: new[] { "UndoesActionId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoordinatorCommandActions");
        }
    }
}
