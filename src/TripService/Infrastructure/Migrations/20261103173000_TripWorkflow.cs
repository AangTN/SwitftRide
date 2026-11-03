using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace SwiftRide.Trip.Infrastructure.Migrations;

[Migration("20261103173000_TripWorkflow")]
[DbContext(typeof(TripDbContext))]
public partial class TripWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "idempotency_key", table: "trips", type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<int>(name: "state_version", table: "trips", type: "int", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(name: "payment_id", table: "trips", type: "nvarchar(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>(name: "payment_status", table: "trips", type: "nvarchar(30)", maxLength: 30, nullable: true);
        migrationBuilder.AddColumn<string>(name: "reason", table: "trip_status_history", type: "nvarchar(max)", nullable: true);
        migrationBuilder.CreateTable(
            name: "trip_proposals",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uniqueidentifier", nullable: false), trip_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                driver_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true), quote_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true), currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                driver_name_snapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true), plate_number_snapshot = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                vehicle_name_snapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true), vehicle_image_key_snapshot = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false), created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                rider_approval_deadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true), rider_responded_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true), driver_response_deadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true), resolved_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
            },
            constraints: table => { table.PrimaryKey("PK_trip_proposals", x => x.id); table.ForeignKey(name: "FK_trip_proposals_trips_trip_id", column: x => x.trip_id, principalTable: "trips", principalColumn: "id", onDelete: ReferentialAction.Restrict); });
        migrationBuilder.CreateIndex(name: "IX_trips_rider_id_idempotency_key", table: "trips", columns: new[] { "rider_id", "idempotency_key" }, unique: true, filter: "[idempotency_key] <> ''");
        migrationBuilder.CreateIndex(name: "IX_trip_proposals_trip_id_driver_id", table: "trip_proposals", columns: new[] { "trip_id", "driver_id" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_trip_proposals_status_driver_response_deadline", table: "trip_proposals", columns: new[] { "status", "driver_response_deadline" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "trip_proposals");
        migrationBuilder.DropIndex(name: "IX_trips_rider_id_idempotency_key", table: "trips");
        migrationBuilder.DropColumn(name: "idempotency_key", table: "trips"); migrationBuilder.DropColumn(name: "state_version", table: "trips");
        migrationBuilder.DropColumn(name: "payment_id", table: "trips"); migrationBuilder.DropColumn(name: "payment_status", table: "trips"); migrationBuilder.DropColumn(name: "reason", table: "trip_status_history");
    }
}
