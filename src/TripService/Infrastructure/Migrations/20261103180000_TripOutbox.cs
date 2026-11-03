using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Trip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TripOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_trip_proposals_trip_id_driver_id",
                table: "trip_proposals");

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                table: "trips",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    trip_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    proposal_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    sent_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trip_proposals_trip_id_driver_id",
                table: "trip_proposals",
                columns: new[] { "trip_id", "driver_id" },
                unique: true,
                filter: "[driver_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_status_next_attempt_at",
                table: "outbox_messages",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_trip_id",
                table: "outbox_messages",
                column: "trip_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_trip_proposals_trip_id_driver_id",
                table: "trip_proposals");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                table: "trips");

            migrationBuilder.CreateIndex(
                name: "IX_trip_proposals_trip_id_driver_id",
                table: "trip_proposals",
                columns: new[] { "trip_id", "driver_id" },
                unique: true);
        }
    }
}
