using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Trip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RiderAndFareSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "rider_name_snapshot",
                table: "trips",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rider_phone_snapshot",
                table: "trips",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "distance_meters",
                table: "trip_proposals",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "estimated_duration_seconds",
                table: "trip_proposals",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pricing_snapshot_json",
                table: "trip_proposals",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rider_name_snapshot",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "rider_phone_snapshot",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "distance_meters",
                table: "trip_proposals");

            migrationBuilder.DropColumn(
                name: "estimated_duration_seconds",
                table: "trip_proposals");

            migrationBuilder.DropColumn(
                name: "pricing_snapshot_json",
                table: "trip_proposals");
        }
    }
}
