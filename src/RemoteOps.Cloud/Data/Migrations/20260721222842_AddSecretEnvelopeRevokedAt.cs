using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteOps.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretEnvelopeRevokedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "secret_envelopes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "secret_envelopes");
        }
    }
}
