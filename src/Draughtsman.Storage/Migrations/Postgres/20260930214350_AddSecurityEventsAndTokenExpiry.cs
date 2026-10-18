using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Draughtsman.Storage.Migrations.Postgres
{
    /// <summary>
    /// AU-04 and AU-05. Adds the instance-level <c>security_events</c> table and, to <c>api_tokens</c>, <c>expires_at</c>
    /// and <c>scope</c>. Existing tokens are grandfathered on purpose: <c>expires_at</c> stays NULL (never expires until
    /// revoked) and <c>scope</c> defaults to 'full', so no agent configuration breaks on upgrade; the tokens page shows
    /// them as having no expiry so their owners can rotate them. Every token created after this has an expiry.
    /// </summary>
    public partial class AddSecurityEventsAndTokenExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                table: "api_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "api_tokens",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "full");

            migrationBuilder.CreateTable(
                name: "security_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    actor_label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    target_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    target_label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    source_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_at",
                table: "security_events",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_security_events_type_at",
                table: "security_events",
                columns: new[] { "type", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_events");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "api_tokens");
        }
    }
}
