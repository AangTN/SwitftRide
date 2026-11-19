using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Trip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OneServingTripPerRider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT [rider_id] FROM [trips]
                    WHERE [status] IN ('Requested', 'Matching', 'AwaitingRiderApproval', 'AwaitingDriverResponse', 'DriverAccepted', 'EnRoute', 'PickedUp')
                    GROUP BY [rider_id] HAVING COUNT(*) > 1
                )
                    THROW 51000, 'Multiple serving trips exist for a rider. Resolve them through the trip workflow before applying OneServingTripPerRider.', 1;
                """);
            migrationBuilder.CreateIndex(
                name: "ux_trips_rider_in_service",
                table: "trips",
                column: "rider_id",
                unique: true,
                filter: "[status] IN ('Requested', 'Matching', 'AwaitingRiderApproval', 'AwaitingDriverResponse', 'DriverAccepted', 'EnRoute', 'PickedUp')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_trips_rider_in_service",
                table: "trips");
        }
    }
}
