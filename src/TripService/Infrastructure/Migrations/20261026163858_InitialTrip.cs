using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Trip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialTrip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trips",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rider_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    driver_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    pickup_address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    pickup_latitude = table.Column<double>(type: "float", nullable: false),
                    pickup_longitude = table.Column<double>(type: "float", nullable: false),
                    destination_address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    destination_latitude = table.Column<double>(type: "float", nullable: false),
                    destination_longitude = table.Column<double>(type: "float", nullable: false),
                    status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    quote_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    agreed_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    driver_name_snapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    plate_number_snapshot = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    vehicle_name_snapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    vehicle_image_key_snapshot = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    driver_accepted_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    picked_up_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    dropped_off_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trips", x => x.id);
                    table.CheckConstraint("ck_trip_amount", "[agreed_amount] IS NULL OR [agreed_amount] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "trip_status_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    trip_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    from_status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trip_status_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_trip_status_history_trips_trip_id",
                        column: x => x.trip_id,
                        principalTable: "trips",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trip_status_history_trip_id_created_at",
                table: "trip_status_history",
                columns: new[] { "trip_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_trips_driver_id_status",
                table: "trips",
                columns: new[] { "driver_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_trips_rider_id_created_at",
                table: "trips",
                columns: new[] { "rider_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trip_status_history");

            migrationBuilder.DropTable(
                name: "trips");
        }
    }
}
