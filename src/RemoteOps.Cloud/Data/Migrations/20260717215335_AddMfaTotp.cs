using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteOps.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMfaTotp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaEnrolledAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "MfaSecret",
                table: "users",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MfaEnrolledAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MfaSecret",
                table: "users");
        }
    }
}
