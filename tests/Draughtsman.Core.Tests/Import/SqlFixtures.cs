namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// Synthetic schemas for the SQL importer tests, written by hand in the style each tool emits (SSMS scripts, pg_dump,
/// mysqldump, sqlite3 .schema). None of it is copied from a real database.
/// </summary>
internal static class SqlFixtures
{
    /// <summary>
    /// SSMS style: batch separators, bracketed and schema-qualified names, inline named constraints, an inline reference
    /// with no column list, a composite primary key and a composite foreign key, collations, identity, defaults with
    /// nested parentheses, and a foreign key to a table that is not in the script (Users).
    /// </summary>
    public const string TSql = """
        /****** Object:  Table [dbo].[Customers]    Script Date: 01/01/2026 00:00:00 ******/
        SET ANSI_NULLS ON
        GO
        SET QUOTED_IDENTIFIER ON
        GO
        create table dbo.Customers
        (
            CustomerId int identity(1,1) not null
                constraint PK_Customers primary key,
            Name nvarchar(100) collate SQL_Latin1_General_CP1_CI_AS not null,
            Email nvarchar(max) null,
            CreatedAt datetime2 not null default (sysutcdatetime()),
            Balance numeric(10,2) not null constraint DF_Customers_Balance default ((0)),
            Nickname nvarchar(50) default 'a,b)' -- a default that looks like syntax
        )
        go

        create table [journey].[Orders]
        (
            OrderId int not null
                constraint PK_Orders primary key,
            CustomerId int not null
                constraint FK_Orders_Customers references Customers,
            CarrierId int null
                constraint FK_Orders_Carriers references dbo.Carriers (CarrierId),
            [Order Note] nvarchar(200) null
        )
        go

        create table dbo.Carriers
        (
            CarrierId int not null constraint PK_Carriers primary key clustered,
            Label nvarchar(40) not null
        )
        go

        create table [journey].OrderLines
        (
            OrderId int not null,
            LineNo int not null,
            Sku nvarchar(20) not null,
            constraint PK_OrderLines primary key (OrderId, LineNo),
            constraint FK_OrderLines_Orders foreign key (OrderId) references journey.Orders (OrderId)
        )
        go

        create table dbo.Shipments
        (
            ShipmentId int not null,
            OrderId int not null,
            LineNo int not null,
            constraint PK_Shipments primary key (ShipmentId),
            constraint FK_Shipments_OrderLines foreign key (OrderId, LineNo) references journey.OrderLines (OrderId, LineNo)
        )
        go

        create table dbo.CustomerProfiles
        (
            CustomerId int not null
                constraint PK_CustomerProfiles primary key
                constraint FK_CustomerProfiles_Customers references dbo.Customers,
            Bio nvarchar(max) null
        )
        go

        create table dbo.Passports
        (
            PassportId int not null constraint PK_Passports primary key,
            CustomerId int not null constraint UQ_Passports_Customer unique,
            constraint FK_Passports_Customers foreign key (CustomerId) references dbo.Customers (CustomerId)
        )
        go

        create table dbo.Audit
        (
            AuditId int not null constraint PK_Audit primary key,
            ActorId int null constraint FK_Audit_Users references dbo.Users
        )
        go

        create table dbo.Notes
        (
            NoteId int not null constraint PK_Notes primary key,
            OrderId int not null
        )
        go
        alter table dbo.Notes with check add constraint FK_Notes_Orders foreign key (OrderId) references journey.Orders (OrderId)
        go
        alter table dbo.Notes check constraint FK_Notes_Orders
        go
        create index IX_Orders_Customer on journey.Orders (CustomerId)
        go
        """;

    public const string Postgres = """
        SET statement_timeout = 0;
        CREATE TABLE public.users (
            id serial PRIMARY KEY,
            email character varying(255) NOT NULL UNIQUE,
            created_at timestamp(3) with time zone DEFAULT now() NOT NULL,
            role text DEFAULT 'member'::text NOT NULL,
            tags text[],
            score numeric(5,2) DEFAULT (1.5)::numeric
        );
        CREATE TABLE posts (
            id bigserial,
            author_id integer NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            editor_id integer,
            title varchar(200) DEFAULT ''::character varying,
            PRIMARY KEY (id)
        );
        ALTER TABLE ONLY public.posts
            ADD CONSTRAINT posts_editor_fk FOREIGN KEY (editor_id) REFERENCES public.users(id) ON DELETE SET NULL;
        CREATE INDEX posts_author_idx ON public.posts USING btree (author_id);
        """;

    public const string MySql = """
        DROP TABLE IF EXISTS `accounts`;
        CREATE TABLE `accounts` (
          `id` int(11) unsigned NOT NULL AUTO_INCREMENT,
          `name` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '',
          `status` enum('active','closed') NOT NULL DEFAULT 'active',
          `updated` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
          PRIMARY KEY (`id`),
          UNIQUE KEY `uq_name` (`name`),
          KEY `idx_status` (`status`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE `invoices` (
          `id` int NOT NULL AUTO_INCREMENT,
          `account_id` int(11) unsigned NOT NULL,
          `memo` text,
          PRIMARY KEY (`id`),
          CONSTRAINT `fk_inv_acc` FOREIGN KEY (`account_id`) REFERENCES `accounts` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
        ) ENGINE=InnoDB;
        """;

    public const string Sqlite = """
        CREATE TABLE artists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);
        CREATE TABLE albums (
            id INTEGER PRIMARY KEY,
            artist_id INTEGER NOT NULL REFERENCES artists(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
            title
        );
        """;
}
