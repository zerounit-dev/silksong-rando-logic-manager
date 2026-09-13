using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Silksong_Rando_Logic_Manager.Data;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

/// <summary>Corrects the unreleased child-alias first pass into flat parent fields.</summary>
[DbContext(typeof(LogicDbContext))]
[Migration("20260905130000_FlattenRequirementCatalogueAliases")]
public partial class FlattenRequirementCatalogueAliases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite rebuilds the parent tables.  Materialize and validate every
        // conversion before either source alias table is dropped. The legacy
        // development baseline has punctuation-bearing source-name aliases. ASCII
        // punctuation is removed by the approved identification rule; unsupported
        // legacy characters abort rather than being guessed or lost.
        migrationBuilder.Sql("CREATE TABLE __RequirementPredicateAliasConversion (ParentId TEXT NOT NULL, AliasId TEXT NOT NULL, SortOrder INTEGER NOT NULL, Alias TEXT NOT NULL, HasUnsupportedUnicode INTEGER NOT NULL, PRIMARY KEY (ParentId, AliasId));");
        migrationBuilder.Sql("CREATE TABLE __RequirementItemAliasConversion (ParentId TEXT NOT NULL, AliasId TEXT NOT NULL, SortOrder INTEGER NOT NULL, Alias TEXT NOT NULL, HasUnsupportedUnicode INTEGER NOT NULL, PRIMARY KEY (ParentId, AliasId));");
        migrationBuilder.Sql("WITH RECURSIVE canonical(ParentId, AliasId, SortOrder, Source, Position, Alias, PendingSpace, HasUnsupportedUnicode) AS (SELECT RequirementPredicateId, Id, SortOrder, Alias, 1, '', 0, 0 FROM RequirementPredicateAliases UNION ALL SELECT ParentId, AliasId, SortOrder, Source, Position + 1, CASE WHEN unicode(substr(Source, Position, 1)) BETWEEN 48 AND 57 OR unicode(substr(Source, Position, 1)) BETWEEN 65 AND 90 OR unicode(substr(Source, Position, 1)) BETWEEN 97 AND 122 THEN Alias || CASE WHEN PendingSpace = 1 THEN ' ' ELSE '' END || substr(Source, Position, 1) ELSE Alias END, CASE WHEN unicode(substr(Source, Position, 1)) IN (9, 10, 11, 12, 13, 32) THEN CASE WHEN length(Alias) > 0 THEN 1 ELSE 0 END WHEN unicode(substr(Source, Position, 1)) BETWEEN 48 AND 57 OR unicode(substr(Source, Position, 1)) BETWEEN 65 AND 90 OR unicode(substr(Source, Position, 1)) BETWEEN 97 AND 122 THEN 0 ELSE PendingSpace END, CASE WHEN HasUnsupportedUnicode = 1 OR unicode(substr(Source, Position, 1)) > 127 THEN 1 ELSE 0 END FROM canonical WHERE Position <= length(Source)) INSERT INTO __RequirementPredicateAliasConversion (ParentId, AliasId, SortOrder, Alias, HasUnsupportedUnicode) SELECT ParentId, AliasId, SortOrder, Alias, HasUnsupportedUnicode FROM canonical WHERE Position > length(Source);");
        migrationBuilder.Sql("WITH RECURSIVE canonical(ParentId, AliasId, SortOrder, Source, Position, Alias, PendingSpace, HasUnsupportedUnicode) AS (SELECT RequirementItemId, Id, SortOrder, Alias, 1, '', 0, 0 FROM RequirementItemAliases UNION ALL SELECT ParentId, AliasId, SortOrder, Source, Position + 1, CASE WHEN unicode(substr(Source, Position, 1)) BETWEEN 48 AND 57 OR unicode(substr(Source, Position, 1)) BETWEEN 65 AND 90 OR unicode(substr(Source, Position, 1)) BETWEEN 97 AND 122 THEN Alias || CASE WHEN PendingSpace = 1 THEN ' ' ELSE '' END || substr(Source, Position, 1) ELSE Alias END, CASE WHEN unicode(substr(Source, Position, 1)) IN (9, 10, 11, 12, 13, 32) THEN CASE WHEN length(Alias) > 0 THEN 1 ELSE 0 END WHEN unicode(substr(Source, Position, 1)) BETWEEN 48 AND 57 OR unicode(substr(Source, Position, 1)) BETWEEN 65 AND 90 OR unicode(substr(Source, Position, 1)) BETWEEN 97 AND 122 THEN 0 ELSE PendingSpace END, CASE WHEN HasUnsupportedUnicode = 1 OR unicode(substr(Source, Position, 1)) > 127 THEN 1 ELSE 0 END FROM canonical WHERE Position <= length(Source)) INSERT INTO __RequirementItemAliasConversion (ParentId, AliasId, SortOrder, Alias, HasUnsupportedUnicode) SELECT ParentId, AliasId, SortOrder, Alias, HasUnsupportedUnicode FROM canonical WHERE Position > length(Source);");
        migrationBuilder.Sql("CREATE TABLE __RequirementCatalogueAliasMigrationGuard (IsValid INTEGER NOT NULL CHECK (IsValid = 1));");
        migrationBuilder.Sql("INSERT INTO __RequirementCatalogueAliasMigrationGuard (IsValid) SELECT CASE WHEN EXISTS (SELECT 1 FROM RequirementPredicates p LEFT JOIN __RequirementPredicateAliasConversion a ON a.ParentId = p.Id GROUP BY p.Id HAVING count(a.AliasId) = 0) OR EXISTS (SELECT 1 FROM RequirementItems i LEFT JOIN __RequirementItemAliasConversion a ON a.ParentId = i.Id GROUP BY i.Id HAVING count(a.AliasId) = 0) OR EXISTS (SELECT 1 FROM __RequirementPredicateAliasConversion WHERE Alias = '' OR Alias GLOB '*[^A-Za-z0-9 ]*' OR HasUnsupportedUnicode = 1) OR EXISTS (SELECT 1 FROM __RequirementItemAliasConversion WHERE Alias = '' OR Alias GLOB '*[^A-Za-z0-9 ]*' OR HasUnsupportedUnicode = 1) OR EXISTS (SELECT 1 FROM __RequirementPredicateAliasConversion GROUP BY lower(Alias) HAVING count(*) > 1) OR EXISTS (SELECT 1 FROM __RequirementItemAliasConversion GROUP BY lower(Alias) HAVING count(*) > 1) THEN 0 ELSE 1 END;");
        migrationBuilder.Sql("CREATE TABLE __RequirementPredicates_flat (Id TEXT NOT NULL CONSTRAINT PK___RequirementPredicates_flat PRIMARY KEY, Name TEXT NOT NULL, Category TEXT NULL, InputSyntax TEXT NOT NULL, OutputSyntax TEXT NOT NULL, Aliases TEXT NOT NULL, Notes TEXT NOT NULL, SortOrder INTEGER NOT NULL);");
        migrationBuilder.Sql("INSERT INTO __RequirementPredicates_flat (Id, Name, Category, InputSyntax, OutputSyntax, Aliases, Notes, SortOrder) SELECT p.Id, p.Name, p.Category, p.InputSyntax, p.OutputSyntax, (SELECT group_concat(Alias, ', ') FROM (SELECT Alias FROM __RequirementPredicateAliasConversion a WHERE a.ParentId = p.Id ORDER BY SortOrder, AliasId)), p.Notes, p.SortOrder FROM RequirementPredicates p;");
        migrationBuilder.Sql("CREATE TABLE __RequirementItems_flat (Id TEXT NOT NULL CONSTRAINT PK___RequirementItems_flat PRIMARY KEY, Name TEXT NOT NULL, Category TEXT NULL, OutputValue TEXT NOT NULL, Aliases TEXT NOT NULL, Notes TEXT NOT NULL, SortOrder INTEGER NOT NULL);");
        migrationBuilder.Sql("INSERT INTO __RequirementItems_flat (Id, Name, Category, OutputValue, Aliases, Notes, SortOrder) SELECT i.Id, i.Name, i.Category, i.OutputValue, (SELECT group_concat(Alias, ', ') FROM (SELECT Alias FROM __RequirementItemAliasConversion a WHERE a.ParentId = i.Id ORDER BY SortOrder, AliasId)), i.Notes, i.SortOrder FROM RequirementItems i;");
        migrationBuilder.Sql("DROP TABLE RequirementPredicateAliases;");
        migrationBuilder.Sql("DROP TABLE RequirementItemAliases;");
        migrationBuilder.Sql("DROP TABLE RequirementPredicates;");
        migrationBuilder.Sql("DROP TABLE RequirementItems;");
        migrationBuilder.Sql("ALTER TABLE __RequirementPredicates_flat RENAME TO RequirementPredicates;");
        migrationBuilder.Sql("ALTER TABLE __RequirementItems_flat RENAME TO RequirementItems;");
        migrationBuilder.Sql("DROP TABLE __RequirementCatalogueAliasMigrationGuard;");
        migrationBuilder.Sql("DROP TABLE __RequirementPredicateAliasConversion;");
        migrationBuilder.Sql("DROP TABLE __RequirementItemAliasConversion;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE TABLE __RequirementPredicates_child (Id TEXT NOT NULL CONSTRAINT PK___RequirementPredicates_child PRIMARY KEY, Name TEXT NOT NULL, Category TEXT NULL, InputSyntax TEXT NOT NULL, OutputSyntax TEXT NOT NULL, Notes TEXT NOT NULL, SortOrder INTEGER NOT NULL);");
        migrationBuilder.Sql("INSERT INTO __RequirementPredicates_child SELECT Id, Name, Category, InputSyntax, OutputSyntax, Notes, SortOrder FROM RequirementPredicates;");
        migrationBuilder.Sql("CREATE TABLE __RequirementItems_child (Id TEXT NOT NULL CONSTRAINT PK___RequirementItems_child PRIMARY KEY, Name TEXT NOT NULL, Category TEXT NULL, OutputValue TEXT NOT NULL, Notes TEXT NOT NULL, SortOrder INTEGER NOT NULL);");
        migrationBuilder.Sql("INSERT INTO __RequirementItems_child SELECT Id, Name, Category, OutputValue, Notes, SortOrder FROM RequirementItems;");
        migrationBuilder.Sql("CREATE TABLE RequirementPredicateAliases (Id TEXT NOT NULL CONSTRAINT PK_RequirementPredicateAliases PRIMARY KEY, RequirementPredicateId TEXT NOT NULL, Alias TEXT NOT NULL, SortOrder INTEGER NOT NULL, CONSTRAINT FK_RequirementPredicateAliases_RequirementPredicates_RequirementPredicateId FOREIGN KEY (RequirementPredicateId) REFERENCES __RequirementPredicates_child(Id) ON DELETE CASCADE);");
        migrationBuilder.Sql("CREATE TABLE RequirementItemAliases (Id TEXT NOT NULL CONSTRAINT PK_RequirementItemAliases PRIMARY KEY, RequirementItemId TEXT NOT NULL, Alias TEXT NOT NULL, SortOrder INTEGER NOT NULL, CONSTRAINT FK_RequirementItemAliases_RequirementItems_RequirementItemId FOREIGN KEY (RequirementItemId) REFERENCES __RequirementItems_child(Id) ON DELETE CASCADE);");
        migrationBuilder.Sql("WITH RECURSIVE aliases(ParentId, Remainder, Alias, SortOrder) AS (SELECT Id, Aliases || ',', '', 0 FROM RequirementPredicates UNION ALL SELECT ParentId, substr(Remainder, instr(Remainder, ',') + 1), trim(substr(Remainder, 1, instr(Remainder, ',') - 1)), SortOrder + 1 FROM aliases WHERE Remainder != '') INSERT INTO RequirementPredicateAliases (Id, RequirementPredicateId, Alias, SortOrder) SELECT substr(ParentId, 1, 24) || printf('%012x', SortOrder - 1), ParentId, Alias, SortOrder - 1 FROM aliases WHERE Alias != ''; ");
        migrationBuilder.Sql("WITH RECURSIVE aliases(ParentId, Remainder, Alias, SortOrder) AS (SELECT Id, Aliases || ',', '', 0 FROM RequirementItems UNION ALL SELECT ParentId, substr(Remainder, instr(Remainder, ',') + 1), trim(substr(Remainder, 1, instr(Remainder, ',') - 1)), SortOrder + 1 FROM aliases WHERE Remainder != '') INSERT INTO RequirementItemAliases (Id, RequirementItemId, Alias, SortOrder) SELECT substr(ParentId, 1, 24) || printf('%012x', SortOrder - 1), ParentId, Alias, SortOrder - 1 FROM aliases WHERE Alias != ''; ");
        migrationBuilder.Sql("CREATE INDEX IX_RequirementPredicateAliases_RequirementPredicateId ON RequirementPredicateAliases (RequirementPredicateId);");
        migrationBuilder.Sql("CREATE INDEX IX_RequirementItemAliases_RequirementItemId ON RequirementItemAliases (RequirementItemId);");
        migrationBuilder.Sql("DROP TABLE RequirementPredicates; DROP TABLE RequirementItems; ALTER TABLE __RequirementPredicates_child RENAME TO RequirementPredicates; ALTER TABLE __RequirementItems_child RENAME TO RequirementItems;");
    }
}
