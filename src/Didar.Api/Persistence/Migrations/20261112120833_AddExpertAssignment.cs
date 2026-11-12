using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Didar.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExpertAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_requests_specialty_id",
                table: "requests");

            migrationBuilder.AddColumn<Guid>(
                name: "expert_id",
                table: "requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "experts",
                columns: table => new
                {
                    expert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    last_assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_experts", x => x.expert_id);
                    table.CheckConstraint("ck_experts_capacity", "capacity >= 0");
                    table.CheckConstraint("ck_experts_status", "status IN ('Active', 'PendingDeactivation', 'Inactive')");
                });

            migrationBuilder.CreateTable(
                name: "expert_specialties",
                columns: table => new
                {
                    expert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialty_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expert_specialties", x => new { x.expert_id, x.specialty_id });
                    table.CheckConstraint("ck_expert_specialties_status", "status IN ('Active', 'PendingDeactivation', 'Inactive')");
                    table.ForeignKey(
                        name: "FK_expert_specialties_experts_expert_id",
                        column: x => x.expert_id,
                        principalTable: "experts",
                        principalColumn: "expert_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expert_specialties_specialties_specialty_id",
                        column: x => x.specialty_id,
                        principalTable: "specialties",
                        principalColumn: "specialty_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requests_expert_id",
                table: "requests",
                column: "expert_id");

            migrationBuilder.CreateIndex(
                name: "IX_requests_specialty_id_created_at_request_id",
                table: "requests",
                columns: new[] { "specialty_id", "created_at", "request_id" });

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM requests WHERE status = 'Assigned' AND expert_id IS NULL) THEN
                        RAISE EXCEPTION 'Cannot add Assigned-requires-expert constraint: existing Assigned requests have no assigned expert. Map the expert before migrating.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_requests_assigned_expert",
                table: "requests",
                sql: "status <> 'Assigned' OR expert_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_requests_pending_expert",
                table: "requests",
                sql: "status <> 'Pending' OR expert_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_expert_specialties_specialty_id",
                table: "expert_specialties",
                column: "specialty_id");

            migrationBuilder.AddForeignKey(
                name: "FK_requests_experts_expert_id",
                table: "requests",
                column: "expert_id",
                principalTable: "experts",
                principalColumn: "expert_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_requests_experts_expert_id",
                table: "requests");

            migrationBuilder.DropTable(
                name: "expert_specialties");

            migrationBuilder.DropTable(
                name: "experts");

            migrationBuilder.DropIndex(
                name: "IX_requests_expert_id",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "IX_requests_specialty_id_created_at_request_id",
                table: "requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_requests_assigned_expert",
                table: "requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_requests_pending_expert",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "expert_id",
                table: "requests");

            migrationBuilder.CreateIndex(
                name: "IX_requests_specialty_id",
                table: "requests",
                column: "specialty_id");
        }
    }
}
