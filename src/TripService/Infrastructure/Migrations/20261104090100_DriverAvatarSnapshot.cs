using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Trip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DriverAvatarSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "driver_avatar_image_key_snapshot",
                table: "trips",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "driver_avatar_image_key_snapshot",
                table: "trip_proposals",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "driver_avatar_image_key_snapshot",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "driver_avatar_image_key_snapshot",
                table: "trip_proposals");
        }
    }
}
