using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20261011200000_DeclarePredicateOperandLists")]
public partial class DeclarePredicateOperandLists : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE RequirementPredicates SET InputSyntax = '{predicate} [{direction}]' WHERE InputSyntax COLLATE BINARY = '{predicate} {direction}';");
        migrationBuilder.Sql("UPDATE RequirementPredicates SET InputSyntax = '{predicate} [{direction?}]' WHERE InputSyntax COLLATE BINARY = '{predicate} {direction?}';");
        migrationBuilder.Sql("UPDATE RequirementPredicates SET InputSyntax = '{difficulty} {predicate} [{enemy}]' WHERE InputSyntax COLLATE BINARY = '{difficulty} {predicate} {enemy}';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE RequirementPredicates SET InputSyntax = '{predicate} {direction}' WHERE InputSyntax COLLATE BINARY = '{predicate} [{direction}]';");
        migrationBuilder.Sql("UPDATE RequirementPredicates SET InputSyntax = '{predicate} {direction?}' WHERE InputSyntax COLLATE BINARY = '{predicate} [{direction?}]';");
        migrationBuilder.Sql("UPDATE RequirementPredicates SET InputSyntax = '{difficulty} {predicate} {enemy}' WHERE InputSyntax COLLATE BINARY = '{difficulty} {predicate} [{enemy}]';");
    }
}
