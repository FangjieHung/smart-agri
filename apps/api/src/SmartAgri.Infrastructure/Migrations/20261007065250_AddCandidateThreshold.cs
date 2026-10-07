using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartAgri.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CandidateMinScore",
                table: "AssistantTestRuns",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsedCandidates",
                table: "AnswerOutcomes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidateMinScore",
                table: "AssistantTestRuns");

            migrationBuilder.DropColumn(
                name: "UsedCandidates",
                table: "AnswerOutcomes");
        }
    }
}
