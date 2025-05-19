using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UMB.Model.Migrations
{
    /// <inheritdoc />
    public partial class multipleaccountsupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppConnections_UserId",
                table: "WhatsAppConnections");

            migrationBuilder.DropIndex(
                name: "IX_PlatformAccounts_UserId",
                table: "PlatformAccounts");

            migrationBuilder.DropColumn(
                name: "ExternalBusinessId",
                table: "PlatformAccounts");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "WhatsAppConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "RefreshToken",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PlatformType",
                table: "PlatformAccounts",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalAccountId",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountIdentifier",
                table: "PlatformAccounts",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PlatformAccounts",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "PlatformAccounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "PlatformAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountIdentifier",
                table: "MessageMetadatas",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsAutoReplied",
                table: "MessageMetadatas",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsNew",
                table: "MessageMetadatas",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConnections_UserId_PhoneNumber",
                table: "WhatsAppConnections",
                columns: new[] { "UserId", "PhoneNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAccounts_UserId_PlatformType_AccountIdentifier",
                table: "PlatformAccounts",
                columns: new[] { "UserId", "PlatformType", "AccountIdentifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppConnections_UserId_PhoneNumber",
                table: "WhatsAppConnections");

            migrationBuilder.DropIndex(
                name: "IX_PlatformAccounts_UserId_PlatformType_AccountIdentifier",
                table: "PlatformAccounts");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "WhatsAppConnections");

            migrationBuilder.DropColumn(
                name: "AccountIdentifier",
                table: "PlatformAccounts");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PlatformAccounts");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "PlatformAccounts");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "PlatformAccounts");

            migrationBuilder.DropColumn(
                name: "AccountIdentifier",
                table: "MessageMetadatas");

            migrationBuilder.DropColumn(
                name: "IsAutoReplied",
                table: "MessageMetadatas");

            migrationBuilder.DropColumn(
                name: "IsNew",
                table: "MessageMetadatas");

            migrationBuilder.AlterColumn<string>(
                name: "RefreshToken",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "PlatformType",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalAccountId",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<string>(
                name: "ExternalBusinessId",
                table: "PlatformAccounts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConnections_UserId",
                table: "WhatsAppConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAccounts_UserId",
                table: "PlatformAccounts",
                column: "UserId");
        }
    }
}
