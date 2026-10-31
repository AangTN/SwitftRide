using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Auth.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BindOtpToClientApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_otp_challenges_request_ip_created_at",
                table: "otp_challenges");

            migrationBuilder.AddColumn<string>(
                name: "client_app",
                table: "otp_challenges",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            // Existing challenges have no trusted app binding and must be requested again.
            // Keep their request history so upgrading does not reset the hourly quota.
            migrationBuilder.Sql("""
                UPDATE otp_challenges SET invalidated_at = now()
                WHERE client_app = '' AND consumed_at IS NULL AND invalidated_at IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_otp_challenges_request_ip_phone_number_created_at",
                table: "otp_challenges",
                columns: new[] { "request_ip", "phone_number", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_otp_challenges_request_ip_phone_number_created_at",
                table: "otp_challenges");

            migrationBuilder.DropColumn(
                name: "client_app",
                table: "otp_challenges");

            migrationBuilder.CreateIndex(
                name: "IX_otp_challenges_request_ip_created_at",
                table: "otp_challenges",
                columns: new[] { "request_ip", "created_at" });
        }
    }
}
