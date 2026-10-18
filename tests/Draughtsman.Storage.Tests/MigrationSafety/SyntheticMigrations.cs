using Microsoft.EntityFrameworkCore.Migrations;

namespace Draughtsman.Storage.Tests.MigrationSafety;

// Migrations that exist only to prove the guard can fail. They carry no [Migration] or [DbContext] attribute, so EF never
// finds them and nothing ships them; the guard's tests construct them directly.

/// <summary>Everything the guard exists to stop, in one migration.</summary>
internal sealed class DeliberatelyDestructiveMigration : Migration
{
    public const string Id = "29990101000000_DeliberatelyDestructive";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "audit");
        migrationBuilder.DropColumn(name: "title", table: "documents");
        migrationBuilder.RenameColumn(name: "name", table: "api_tokens", newName: "label");
        migrationBuilder.RenameTable(name: "assets", newName: "images");
        migrationBuilder.AlterColumn<string>(name: "email", table: "users", type: "TEXT", maxLength: 8, nullable: false, oldClrType: typeof(string), oldType: "TEXT", oldMaxLength: 256, oldNullable: false);
        migrationBuilder.AlterColumn<int>(name: "role", table: "users", type: "INTEGER", nullable: false, oldClrType: typeof(string), oldType: "TEXT", oldNullable: false);
        migrationBuilder.AlterColumn<string>(name: "scope", table: "api_tokens", type: "TEXT", nullable: false, oldClrType: typeof(string), oldType: "TEXT", oldNullable: true);
        migrationBuilder.AddColumn<string>(name: "must_have", table: "documents", type: "TEXT", nullable: false);
        migrationBuilder.Sql("DELETE FROM sessions WHERE revoked = 1;");
        migrationBuilder.Sql("TRUNCATE TABLE security_events;");
        migrationBuilder.Sql("ALTER TABLE users DROP COLUMN display_name;");
        migrationBuilder.DeleteData(table: "users", keyColumn: "id", keyValue: "usr_1");
    }
}

/// <summary>The control: only expands, widens, backfills.</summary>
internal sealed class PurelyAdditiveMigration : Migration
{
    public const string Id = "29990102000000_PurelyAdditive";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "widgets",
            columns: table => new { id = table.Column<string>(type: "TEXT", nullable: false), size = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0) },
            constraints: table => table.PrimaryKey("pk_widgets", x => x.id));
        migrationBuilder.AddColumn<string>(name: "note", table: "documents", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>(name: "weight", table: "documents", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AlterColumn<string>(name: "email", table: "users", type: "TEXT", maxLength: 512, nullable: false, oldClrType: typeof(string), oldType: "TEXT", oldMaxLength: 256, oldNullable: false);
        migrationBuilder.AlterColumn<string>(name: "scope", table: "api_tokens", type: "TEXT", nullable: true, oldClrType: typeof(string), oldType: "TEXT", oldNullable: false);
        migrationBuilder.CreateIndex(name: "ix_widgets_size", table: "widgets", column: "size");
        migrationBuilder.Sql("UPDATE documents SET weight = 1 WHERE weight IS NULL; -- DELETE comments are not statements");
    }
}
