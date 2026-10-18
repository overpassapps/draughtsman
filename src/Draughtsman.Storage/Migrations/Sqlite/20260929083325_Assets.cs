using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Draughtsman.Storage.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Assets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_hash",
                table: "assets",
                column: "hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assets");
        }
    }
}
