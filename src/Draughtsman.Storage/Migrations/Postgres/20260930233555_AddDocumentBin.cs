using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Draughtsman.Storage.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddDocumentBin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by_label",
                table: "documents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by_user_id",
                table: "documents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_deleted_at",
                table: "documents",
                column: "deleted_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documents_deleted_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "deleted_by_label",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "deleted_by_user_id",
                table: "documents");
        }
    }
}
