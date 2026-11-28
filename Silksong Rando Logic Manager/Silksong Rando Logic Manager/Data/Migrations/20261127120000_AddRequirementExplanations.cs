using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20261127120000_AddRequirementExplanations")]
public sealed class AddRequirementExplanations : Migration
{
    private const string EntityName = "Silksong_Rando_Logic_Manager.Data.RequirementExplanation";
    private const string RoomName = "Silksong_Rando_Logic_Manager.Data.Room";
    private const string Table = "RequirementExplanations";
    private const string Columns = "Id CreatedUtc UpdatedUtc IsArchived ArchivedUtc RoomId ReferenceId FriendlyName Link Notes SortOrder";
    private const string Wall = "(621355968000000000 + CAST(strftime('%s','now') AS INTEGER)*10000000 + CAST(substr(strftime('%f','now'),4,3) AS INTEGER)*10000)";
    private const string Maximum = "3155378975999999999";
    private static string Advance => $"CASE WHEN typeof(CacheFlagUtc) != 'integer' OR CacheFlagUtc NOT BETWEEN 0 AND {Maximum} OR CacheFlagUtc = {Maximum} OR typeof({Wall}) != 'integer' OR {Wall} NOT BETWEEN 1 AND {Maximum} THEN RAISE(ABORT, 'Room freshness overflow or invalid clock') ELSE max(CacheFlagUtc + 1, {Wall}) END";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: Table,
            columns: t => new
            {
                Id = t.Column<Guid>(type: "TEXT", nullable: false),
                ArchivedUtc = t.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedUtc = t.Column<DateTime>(type: "TEXT", nullable: false),
                FriendlyName = t.Column<string>(type: "TEXT", nullable: false),
                IsArchived = t.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                Link = t.Column<string>(type: "TEXT", nullable: false),
                Notes = t.Column<string>(type: "TEXT", nullable: false),
                ReferenceId = t.Column<string>(type: "TEXT", nullable: false),
                RoomId = t.Column<Guid>(type: "TEXT", nullable: false),
                SortOrder = t.Column<int>(type: "INTEGER", nullable: false),
                UpdatedUtc = t.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: t =>
            {
                t.PrimaryKey("PK_RequirementExplanations", x => x.Id);
                t.ForeignKey("FK_RequirementExplanations_Rooms_RoomId", x => x.RoomId, "Rooms", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_RequirementExplanations_RoomId", Table, "RoomId");

        var changed = string.Join(" OR ", Columns.Split(' ').Select(column =>
            $"(typeof(OLD.\"{column}\") != typeof(NEW.\"{column}\") OR OLD.\"{column}\" COLLATE BINARY IS NOT NEW.\"{column}\" COLLATE BINARY)"));
        migrationBuilder.Sql($"CREATE TRIGGER CF_{Table}_Insert AFTER INSERT ON \"{Table}\" BEGIN UPDATE Rooms SET CacheFlagUtc = {Advance} WHERE Id = NEW.RoomId; END;");
        migrationBuilder.Sql($"CREATE TRIGGER CF_{Table}_Update AFTER UPDATE ON \"{Table}\" WHEN {changed} BEGIN UPDATE Rooms SET CacheFlagUtc = {Advance} WHERE Id IN (OLD.RoomId, NEW.RoomId); END;");
        migrationBuilder.Sql($"CREATE TRIGGER CF_{Table}_Delete AFTER DELETE ON \"{Table}\" BEGIN UPDATE Rooms SET CacheFlagUtc = {Advance} WHERE Id = OLD.RoomId; END;");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Requirement explanations are patch-forward only; restore a compatible backup instead of downgrading.");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildFrozenModel(modelBuilder);

    internal static void BuildFrozenModel(ModelBuilder modelBuilder)
    {
        AddRoomCompletionStatus.BuildFrozenModel(modelBuilder);
        modelBuilder.Entity(EntityName, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            b.Property<DateTime?>("ArchivedUtc").HasColumnType("TEXT");
            b.Property<DateTime>("CreatedUtc").HasColumnType("TEXT");
            b.Property<string>("FriendlyName").IsRequired().HasColumnType("TEXT");
            b.Property<bool>("IsArchived").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasDefaultValue(false);
            b.Property<string>("Link").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Notes").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ReferenceId").IsRequired().HasColumnType("TEXT");
            b.Property<Guid>("RoomId").HasColumnType("TEXT");
            b.Property<int>("SortOrder").HasColumnType("INTEGER");
            b.Property<DateTime>("UpdatedUtc").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("RoomId");
            b.ToTable(Table);
            b.HasOne(RoomName, "Room").WithMany(Table).HasForeignKey("RoomId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.Navigation("Room");
        });
        modelBuilder.Entity(RoomName).Navigation(Table);
    }
}
