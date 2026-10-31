using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.Auth.Infrastructure.Migrations;

/// <summary>Replace role-name foreign keys with UUIDs without losing existing assignments.</summary>
public partial class AddRoleIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "id", table: "roles", type: "uuid", nullable: true);
        migrationBuilder.Sql("""
            UPDATE roles SET id = CASE name
                WHEN 'Rider' THEN '10000000-0000-0000-0000-000000000001'::uuid
                WHEN 'Driver' THEN '10000000-0000-0000-0000-000000000002'::uuid
                WHEN 'Admin' THEN '10000000-0000-0000-0000-000000000003'::uuid
                ELSE gen_random_uuid()
            END;
            """);
        migrationBuilder.AddColumn<Guid>(name: "role_id", table: "user_roles", type: "uuid", nullable: true);
        migrationBuilder.Sql("""
            UPDATE user_roles AS ur SET role_id = r.id
            FROM roles AS r WHERE ur.role_name = r.name;
            """);
        migrationBuilder.DropForeignKey(name: "FK_user_roles_roles_role_name", table: "user_roles");
        migrationBuilder.DropPrimaryKey(name: "PK_user_roles", table: "user_roles");
        migrationBuilder.DropIndex(name: "IX_user_roles_role_name", table: "user_roles");
        migrationBuilder.DropPrimaryKey(name: "PK_roles", table: "roles");
        migrationBuilder.AlterColumn<Guid>(name: "id", table: "roles", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AlterColumn<Guid>(name: "role_id", table: "user_roles", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AddPrimaryKey(name: "PK_roles", table: "roles", column: "id");
        migrationBuilder.AddPrimaryKey(name: "PK_user_roles", table: "user_roles", columns: new[] { "user_id", "role_id" });
        migrationBuilder.CreateIndex(name: "IX_roles_name", table: "roles", column: "name", unique: true);
        migrationBuilder.CreateIndex(name: "IX_user_roles_role_id", table: "user_roles", column: "role_id");
        migrationBuilder.AddForeignKey(name: "FK_user_roles_roles_role_id", table: "user_roles", column: "role_id",
            principalTable: "roles", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.DropColumn(name: "role_name", table: "user_roles");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "role_name", table: "user_roles", type: "character varying(20)",
            maxLength: 20, nullable: true);
        migrationBuilder.Sql("""
            UPDATE user_roles AS ur SET role_name = r.name
            FROM roles AS r WHERE ur.role_id = r.id;
            """);
        migrationBuilder.DropForeignKey(name: "FK_user_roles_roles_role_id", table: "user_roles");
        migrationBuilder.DropPrimaryKey(name: "PK_user_roles", table: "user_roles");
        migrationBuilder.DropIndex(name: "IX_user_roles_role_id", table: "user_roles");
        migrationBuilder.DropPrimaryKey(name: "PK_roles", table: "roles");
        migrationBuilder.DropIndex(name: "IX_roles_name", table: "roles");
        migrationBuilder.AlterColumn<string>(name: "role_name", table: "user_roles", type: "character varying(20)",
            maxLength: 20, nullable: false, oldClrType: typeof(string), oldType: "character varying(20)",
            oldMaxLength: 20, oldNullable: true);
        migrationBuilder.AddPrimaryKey(name: "PK_roles", table: "roles", column: "name");
        migrationBuilder.AddPrimaryKey(name: "PK_user_roles", table: "user_roles", columns: new[] { "user_id", "role_name" });
        migrationBuilder.CreateIndex(name: "IX_user_roles_role_name", table: "user_roles", column: "role_name");
        migrationBuilder.AddForeignKey(name: "FK_user_roles_roles_role_name", table: "user_roles", column: "role_name",
            principalTable: "roles", principalColumn: "name", onDelete: ReferentialAction.Restrict);
        migrationBuilder.DropColumn(name: "role_id", table: "user_roles");
        migrationBuilder.DropColumn(name: "id", table: "roles");
    }
}
