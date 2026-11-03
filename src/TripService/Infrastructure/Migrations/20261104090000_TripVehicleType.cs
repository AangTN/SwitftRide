using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Trip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TripVehicleType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vehicle_type",
                table: "trips",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Motorbike");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vehicle_type",
                table: "trips");
        }
    }
}
