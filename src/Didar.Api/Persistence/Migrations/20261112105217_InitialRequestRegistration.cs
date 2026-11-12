using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Didar.Api.Persistence.Migrations
{
    public partial class InitialRequestRegistration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:C", "C,C,libc,True");

            migrationBuilder.CreateTable(
                name: "specialties",
                columns: table => new
                {
                    specialty_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specialties", x => x.specialty_id);
                });

            migrationBuilder.CreateTable(
                name: "requests",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_key = table.Column<string>(type: "text COLLATE \"C\"", nullable: false),
                    specialty_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requests", x => x.request_id);
                    table.CheckConstraint("ck_requests_priority", "priority IN ('Normal', 'Urgent')");
                    table.CheckConstraint("ck_requests_request_key_length", "octet_length(request_key) BETWEEN 1 AND 1024");
                    table.CheckConstraint("ck_requests_status", "status IN ('Pending', 'Assigned', 'Closed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_requests_specialties_specialty_id",
                        column: x => x.specialty_id,
                        principalTable: "specialties",
                        principalColumn: "specialty_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requests_request_key",
                table: "requests",
                column: "request_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requests_specialty_id",
                table: "requests",
                column: "specialty_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "requests");

            migrationBuilder.DropTable(
                name: "specialties");
        }
    }
}
