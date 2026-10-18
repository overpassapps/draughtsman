using Draughtsman.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Storage;

/// <summary>
/// The schema, shared by every provider. Table and column names are snake_case per spec section 7 and set explicitly
/// here rather than through a naming-convention package. Each provider has a subclass so its migrations are keyed to
/// its own context type; nothing outside this assembly sees a DbContext (ADR 0003).
/// </summary>
public abstract class DraughtsmanDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    public DbSet<DocumentUpdateRow> DocumentUpdates => Set<DocumentUpdateRow>();

    public DbSet<DocumentVersionRow> DocumentVersions => Set<DocumentVersionRow>();

    public DbSet<AuditRow> Audit => Set<AuditRow>();

    public DbSet<SecurityEventRow> SecurityEvents => Set<SecurityEventRow>();

    public DbSet<AssetRow> Assets => Set<AssetRow>();

    public DbSet<UserRow> Users => Set<UserRow>();

    public DbSet<ApiTokenRow> ApiTokens => Set<ApiTokenRow>();

    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    public DbSet<PasswordResetTokenRow> PasswordResetTokens => Set<PasswordResetTokenRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DocumentRow>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(d => d.Title).HasColumnName("title");
            entity.Property(d => d.TitleSearch).HasColumnName("title_search");
            entity.Property(d => d.Type).HasColumnName("type").HasMaxLength(32);
            entity.Property(d => d.Workspace).HasColumnName("workspace").HasMaxLength(128);
            entity.Property(d => d.Snapshot).HasColumnName("snapshot");
            entity.Property(d => d.SchemaVersion).HasColumnName("schema_version");
            entity.Property(d => d.Etag).HasColumnName("etag").HasMaxLength(64);
            entity.Property(d => d.CreatedAt).HasColumnName("created_at");
            entity.Property(d => d.UpdatedAt).HasColumnName("updated_at");
            entity.Property(d => d.DeletedAt).HasColumnName("deleted_at");
            entity.Property(d => d.DeletedByUserId).HasColumnName("deleted_by_user_id").HasMaxLength(128);
            entity.Property(d => d.DeletedByLabel).HasColumnName("deleted_by_label").HasMaxLength(256);
            entity.HasIndex(d => new { d.Workspace, d.UpdatedAt }).HasDatabaseName("ix_documents_workspace_updated_at");
            entity.HasIndex(d => d.DeletedAt).HasDatabaseName("ix_documents_deleted_at");
        });

        modelBuilder.Entity<DocumentUpdateRow>(entity =>
        {
            entity.ToTable("document_updates");
            entity.HasKey(u => new { u.DocumentId, u.Seq });
            entity.Property(u => u.DocumentId).HasColumnName("document_id").HasMaxLength(128);
            entity.Property(u => u.Seq).HasColumnName("seq").ValueGeneratedNever();
            entity.Property(u => u.Update).HasColumnName("update");
            entity.Property(u => u.CreatedAt).HasColumnName("created_at");
            entity.Property(u => u.Actor).HasColumnName("actor").HasMaxLength(512);
            entity.HasOne(u => u.Document).WithMany(d => d.Updates).HasForeignKey(u => u.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentVersionRow>(entity =>
        {
            entity.ToTable("document_versions");
            entity.HasKey(v => v.Id);
            entity.Property(v => v.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(v => v.DocumentId).HasColumnName("document_id").HasMaxLength(128);
            entity.Property(v => v.Name).HasColumnName("name").HasMaxLength(256);
            entity.Property(v => v.Snapshot).HasColumnName("snapshot");
            entity.Property(v => v.CreatedAt).HasColumnName("created_at");
            entity.Property(v => v.Actor).HasColumnName("actor").HasMaxLength(512);
            entity.Property(v => v.Reason).HasColumnName("reason");
            entity.HasIndex(v => new { v.DocumentId, v.CreatedAt }).HasDatabaseName("ix_document_versions_document_id_created_at");
            entity.HasOne(v => v.Document).WithMany(d => d.Versions).HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditRow>(entity =>
        {
            entity.ToTable("audit");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(a => a.DocumentId).HasColumnName("document_id").HasMaxLength(128);
            entity.Property(a => a.Actor).HasColumnName("actor").HasMaxLength(256);
            entity.Property(a => a.ActorKind).HasColumnName("actor_kind").HasMaxLength(16);
            entity.Property(a => a.Model).HasColumnName("model").HasMaxLength(256);
            entity.Property(a => a.Action).HasColumnName("action").HasMaxLength(64);
            entity.Property(a => a.At).HasColumnName("at");
            entity.Property(a => a.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(128);
            entity.HasIndex(a => new { a.DocumentId, a.At }).HasDatabaseName("ix_audit_document_id_at");
        });

        modelBuilder.Entity<SecurityEventRow>(entity =>
        {
            entity.ToTable("security_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Type).HasColumnName("type").HasMaxLength(64);
            entity.Property(e => e.At).HasColumnName("at");
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(128);
            entity.Property(e => e.ActorLabel).HasColumnName("actor_label").HasMaxLength(256);
            entity.Property(e => e.TargetUserId).HasColumnName("target_user_id").HasMaxLength(128);
            entity.Property(e => e.TargetLabel).HasColumnName("target_label").HasMaxLength(256);
            entity.Property(e => e.SourceIp).HasColumnName("source_ip").HasMaxLength(64);
            entity.Property(e => e.Method).HasColumnName("method").HasMaxLength(16);
            entity.Property(e => e.Detail).HasColumnName("detail").HasMaxLength(512);
            entity.HasIndex(e => e.At).HasDatabaseName("ix_security_events_at");
            entity.HasIndex(e => new { e.Type, e.At }).HasDatabaseName("ix_security_events_type_at");
        });

        modelBuilder.Entity<AssetRow>(entity =>
        {
            entity.ToTable("assets");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(a => a.Hash).HasColumnName("hash").HasMaxLength(64);
            entity.Property(a => a.ContentType).HasColumnName("content_type").HasMaxLength(128);
            entity.Property(a => a.Path).HasColumnName("path").HasMaxLength(512);
            entity.Property(a => a.Size).HasColumnName("size");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at");
            // Unique, so two uploads of the same bytes cannot both insert: the loser is told and returns the winner.
            entity.HasIndex(a => a.Hash).HasDatabaseName("ix_assets_hash").IsUnique();
        });

        modelBuilder.Entity<UserRow>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(u => u.Email).HasColumnName("email").HasMaxLength(256);
            entity.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            entity.Property(u => u.Role).HasColumnName("role").HasMaxLength(16);
            entity.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(512);
            entity.Property(u => u.CreatedAt).HasColumnName("created_at");
            entity.Property(u => u.LastSignInAt).HasColumnName("last_sign_in_at");
            entity.Property(u => u.Active).HasColumnName("active");
            // Email is the sign-in identifier; the store folds case before every lookup and write, so a case-only
            // duplicate is caught by this same index rather than needing a second, collation-specific one.
            entity.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ix_users_email");
        });

        modelBuilder.Entity<ApiTokenRow>(entity =>
        {
            entity.ToTable("api_tokens");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(t => t.UserId).HasColumnName("user_id").HasMaxLength(128);
            entity.Property(t => t.Name).HasColumnName("name").HasMaxLength(256);
            entity.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
            entity.Property(t => t.CreatedAt).HasColumnName("created_at");
            entity.Property(t => t.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(t => t.Revoked).HasColumnName("revoked");
            entity.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            // Existing rows take 'full' from the migration's default: a token minted before scopes existed had the owner's whole role.
            entity.Property(t => t.Scope).HasColumnName("scope").HasMaxLength(16).HasDefaultValue("full");
            entity.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ix_api_tokens_token_hash");
            entity.HasIndex(t => t.UserId).HasDatabaseName("ix_api_tokens_user_id");
            entity.HasOne(t => t.User).WithMany(u => u.ApiTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionRow>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(s => s.UserId).HasColumnName("user_id").HasMaxLength(128);
            entity.Property(s => s.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
            entity.Property(s => s.CreatedAt).HasColumnName("created_at");
            entity.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            entity.Property(s => s.Revoked).HasColumnName("revoked");
            entity.HasIndex(s => s.TokenHash).IsUnique().HasDatabaseName("ix_sessions_token_hash");
            entity.HasIndex(s => s.UserId).HasDatabaseName("ix_sessions_user_id");
            entity.HasOne(s => s.User).WithMany(u => u.Sessions).HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PasswordResetTokenRow>(entity =>
        {
            entity.ToTable("password_reset_tokens");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasColumnName("id").HasMaxLength(128);
            entity.Property(t => t.UserId).HasColumnName("user_id").HasMaxLength(128);
            entity.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
            entity.Property(t => t.Purpose).HasColumnName("purpose").HasMaxLength(16);
            entity.Property(t => t.CreatedAt).HasColumnName("created_at");
            entity.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            entity.Property(t => t.UsedAt).HasColumnName("used_at");
            entity.Property(t => t.InvalidatedAt).HasColumnName("invalidated_at");
            entity.Property(t => t.CreatedByUserId).HasColumnName("created_by_user_id").HasMaxLength(128);
            entity.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ix_password_reset_tokens_token_hash");
            entity.HasIndex(t => new { t.UserId, t.CreatedAt }).HasDatabaseName("ix_password_reset_tokens_user_id_created_at");
            entity.HasOne(t => t.User).WithMany(u => u.PasswordResetTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
