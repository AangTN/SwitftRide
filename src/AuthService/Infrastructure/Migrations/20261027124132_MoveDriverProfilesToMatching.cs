using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace SwiftRide.Auth.Infrastructure.Migrations;

public partial class MoveDriverProfilesToMatching : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Retain a frozen backup; the cross-database copy is verified by Migrate-DriverProfiles.ps1.
        migrationBuilder.DropForeignKey(name: "FK_driver_profiles_users_user_id", table: "driver_profiles");
        migrationBuilder.RenameTable(name: "driver_profiles", newName: "legacy_driver_profiles");
        migrationBuilder.AddColumn<string>(name: "legal_name", table: "legacy_driver_profiles", type: "character varying(150)", maxLength: 150, nullable: false, defaultValue: "");
        migrationBuilder.Sql("""
            UPDATE legacy_driver_profiles p SET legal_name = u.full_name FROM users u WHERE p.user_id = u.id;
            UPDATE auth_sessions SET revoked_at = now(), revocation_reason = 'SeedRoleMigration'
            WHERE user_id = '22222222-2222-2222-2222-222222222222'::uuid AND revoked_at IS NULL
              AND EXISTS (SELECT 1 FROM users WHERE id = auth_sessions.user_id AND phone_number = '+84900000002');
            DELETE FROM user_roles WHERE user_id = '22222222-2222-2222-2222-222222222222'::uuid
              AND role_id = '10000000-0000-0000-0000-000000000001'::uuid
              AND EXISTS (SELECT 1 FROM users WHERE id = user_roles.user_id AND phone_number = '+84900000002');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restores the archived source, not any newer edits made in Matching.
        migrationBuilder.DropColumn(name: "legal_name", table: "legacy_driver_profiles");
        migrationBuilder.RenameTable(name: "legacy_driver_profiles", newName: "driver_profiles");
        migrationBuilder.AddForeignKey(name: "FK_driver_profiles_users_user_id", table: "driver_profiles", column: "user_id", principalTable: "users", principalColumn: "id", onDelete: ReferentialAction.Cascade);
    }
}
