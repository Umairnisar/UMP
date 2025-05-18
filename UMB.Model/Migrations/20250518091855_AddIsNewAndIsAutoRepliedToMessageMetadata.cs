using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UMB.Model.Migrations
{
    /// <inheritdoc />
    public partial class AddIsNewAndIsAutoRepliedToMessageMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
           

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

          
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            
            migrationBuilder.DropColumn(
                name: "IsAutoReplied",
                table: "MessageMetadatas");

            migrationBuilder.DropColumn(
                name: "IsNew",
                table: "MessageMetadatas");

           
        }
    }
}
