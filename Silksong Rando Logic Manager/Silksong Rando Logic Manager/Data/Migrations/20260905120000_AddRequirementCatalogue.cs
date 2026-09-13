using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

public partial class AddRequirementCatalogue : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RequirementItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Category = table.Column<string>(type: "TEXT", nullable: true),
                OutputValue = table.Column<string>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RequirementItems", x => x.Id));

        migrationBuilder.CreateTable(
            name: "RequirementPredicates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Category = table.Column<string>(type: "TEXT", nullable: true),
                InputSyntax = table.Column<string>(type: "TEXT", nullable: false),
                OutputSyntax = table.Column<string>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RequirementPredicates", x => x.Id));

        migrationBuilder.CreateTable(
            name: "RequirementItemAliases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RequirementItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Alias = table.Column<string>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RequirementItemAliases", x => x.Id);
                table.ForeignKey("FK_RequirementItemAliases_RequirementItems_RequirementItemId", x => x.RequirementItemId, "RequirementItems", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RequirementPredicateAliases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RequirementPredicateId = table.Column<Guid>(type: "TEXT", nullable: false),
                Alias = table.Column<string>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RequirementPredicateAliases", x => x.Id);
                table.ForeignKey("FK_RequirementPredicateAliases_RequirementPredicates_RequirementPredicateId", x => x.RequirementPredicateId, "RequirementPredicates", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_RequirementItemAliases_RequirementItemId", "RequirementItemAliases", "RequirementItemId");
        migrationBuilder.CreateIndex("IX_RequirementPredicateAliases_RequirementPredicateId", "RequirementPredicateAliases", "RequirementPredicateId");

        for (var sortOrder = 0; sortOrder < Predicates.Length; sortOrder++)
        {
            var predicate = Predicates[sortOrder];
            var key = NormalizeIdentification(predicate.Name);
            var predicateId = UuidV5($"predicate:{key}");
            migrationBuilder.InsertData("RequirementPredicates", new[] { "Id", "Name", "Category", "InputSyntax", "OutputSyntax", "Notes", "SortOrder" }, new object[] { predicateId, predicate.Name, predicate.Category, predicate.InputSyntax, predicate.OutputSyntax, string.Empty, sortOrder });

            for (var aliasOrder = 0; aliasOrder < predicate.Aliases.Length; aliasOrder++)
            {
                var alias = predicate.Aliases[aliasOrder];
                migrationBuilder.InsertData("RequirementPredicateAliases", new[] { "Id", "RequirementPredicateId", "Alias", "SortOrder" }, new object[] { UuidV5($"predicate-alias:{key}:{NormalizeIdentification(alias)}"), predicateId, alias, aliasOrder });
            }
        }

        var itemRows = ItemRows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var sortOrder = 0; sortOrder < itemRows.Length; sortOrder++)
        {
            var separator = itemRows[sortOrder].LastIndexOf('|');
            var name = itemRows[sortOrder][..separator];
            var category = itemRows[sortOrder][(separator + 1)..];
            var itemId = UuidV5($"item:{sortOrder}:{name}");
            migrationBuilder.InsertData("RequirementItems", new[] { "Id", "Name", "Category", "OutputValue", "Notes", "SortOrder" }, new object[] { itemId, name, category, ToOutputValue(name), string.Empty, sortOrder });
            migrationBuilder.InsertData("RequirementItemAliases", new[] { "Id", "RequirementItemId", "Alias", "SortOrder" }, new object[] { UuidV5($"item-alias:{sortOrder}:{NormalizeIdentification(name)}"), itemId, name, 0 });
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RequirementItemAliases");
        migrationBuilder.DropTable(name: "RequirementPredicateAliases");
        migrationBuilder.DropTable(name: "RequirementItems");
        migrationBuilder.DropTable(name: "RequirementPredicates");
    }

    private static string NormalizeIdentification(string value)
    {
        var result = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetter(character) || char.IsDigit(character))
            {
                if (pendingSpace && result.Length > 0) result.Append(' ');
                result.Append(character);
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(character)) pendingSpace = true;
        }

        return result.ToString();
    }

    private static string ToOutputValue(string value)
    {
        var result = new StringBuilder();
        var pendingHyphen = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetter(character) || char.IsDigit(character))
            {
                if (pendingHyphen && result.Length > 0) result.Append('-');
                result.Append(character);
                pendingHyphen = false;
            }
            else pendingHyphen = true;
        }

        return result.ToString();
    }

    private static Guid UuidV5(string key)
    {
        byte[] namespaceBytes = [0xc9, 0xf5, 0x4e, 0x78, 0x10, 0xd7, 0x5f, 0x88, 0x9b, 0xa2, 0x56, 0xc9, 0x55, 0xbd, 0x1f, 0x9f];
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var input = new byte[namespaceBytes.Length + keyBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        keyBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(hash);
        return Guid.Parse($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}");
    }

    private sealed record PredicateRow(string Name, string Category, string[] Aliases, string InputSyntax, string OutputSyntax);

    private static readonly PredicateRow[] Predicates =
    [
        new("none", "Built-in", ["none"], "{predicate}", "none"),
        new("impassable", "Built-in", ["impassable"], "{predicate}", "impassable"),
        new("needolin", "Ability", ["needolin"], "{predicate}", "capability:needolin"),
        new("sprint", "Ability", ["sprint", "run"], "{predicate}", "capability:sprint"),
        new("dash", "Ability", ["dash"], "{predicate}", "capability:dash"),
        new("cling grip", "Ability", ["cling grip"], "{predicate}", "capability:cling-grip"),
        new("drifter's cloak", "Ability", ["Drifter's Cloak", "drifters"], "{predicate}", "capability:drifters-cloak"),
        new("faydown cloak", "Ability", ["faydown cloak", "faydown"], "{predicate}", "capability:faydown-cloak"),
        new("silk soar", "Ability", ["silk soar"], "{predicate}", "capability:silk-soar"),
        new("clawline", "Ability", ["clawline"], "{predicate}", "capability:clawline"),
        new("sharpdart", "Ability", ["sharpdart"], "{predicate}", "capability:sharpdart"),
        new("ledge grab", "Ability", ["ledge grab"], "{predicate}", "capability:ledge-grab"),
        new("swim", "Ability", ["swim"], "{predicate}", "capability:swim"),
        new("beast crest", "Crest", ["beast crest"], "{predicate}", "capability:beast-crest"),
        new("shaman crest", "Crest", ["shaman crest"], "{predicate}", "capability:shaman-crest"),
        new("architect crest", "Crest", ["architect crest"], "{predicate}", "capability:architect-crest"),
        new("crest pogo", "Technique", ["crest pogo"], "{difficulty} {predicate}", "technique:crest-pogo:{difficulty}"),
        new("spike pogo", "Technique", ["spike pogo"], "{difficulty} {predicate}", "technique:spike-pogo:{difficulty}"),
        new("heal stall", "Technique", ["heal stall"], "{difficulty} {predicate}", "technique:heal-stall:{difficulty}"),
        new("flea brew stall", "Technique", ["flea brew stall"], "{difficulty} {predicate}", "technique:flea-brew-stall:{difficulty}"),
        new("tool skip", "Technique", ["tool skip"], "{difficulty} {predicate}", "technique:tool-skip:{difficulty}"),
        new("scuttlebrace", "Technique", ["scuttlebrace"], "{difficulty?} {predicate}", "technique:scuttlebrace:{difficulty}"),
        new("enemy pogo", "Technique", ["enemy pogo"], "{difficulty?} {predicate}", "technique:enemy-pogo:{difficulty}"),
        new("break wall", "Interaction", ["break wall"], "{predicate} {direction}", "capability:break-wall:{direction}"),
        new("break vines", "Interaction", ["break vines"], "{predicate} {direction}", "capability:break-vines:{direction}"),
        new("activate switch", "Interaction", ["activate switch"], "{predicate} {direction}", "capability:activate-switch:{direction}"),
        new("completed", "Dependency", ["completed"], "{predicate} {check}", "location:{check}"),
        new("defeat", "Dependency", ["defeat", "defeated"], "{predicate} {check}", "location:{check}"),
        new("have", "Inventory", ["have"], "{predicate} {item}", "item:{item}"),
        new("mossberries", "Count", ["mossberries"], "{predicate} {quantity}", "count:mossberries:{quantity}"),
        new("pollip hearts", "Count", ["pollip hearts"], "{predicate} {quantity}", "count:pollip-hearts:{quantity}"),
        new("spool fragments", "Count", ["spool fragments"], "{predicate} {quantity}", "count:spool-fragments:{quantity}"),
        new("progressive silkheart", "Count", ["progressive silkheart"], "{predicate} {quantity}", "count:progressive-silkheart:{quantity}"),
        new("progressive needle upgrade", "Count", ["progressive needle upgrade"], "{predicate} {quantity}", "count:progressive-needle-upgrade:{quantity}"),
        new("rosaries", "Count", ["rosaries"], "{predicate} {quantity}", "count:rosaries:{quantity}"),
        new("tool slots unlocked", "Count", ["tool slots unlocked"], "{predicate} {quantity}", "count:tool-slots-unlocked:{quantity}"),
        new("hunter's journal entries", "Count", ["Hunter's Journal Entries"], "{predicate} {quantity}", "count:hunter-journal-entries:{quantity}")
    ];

    private const string ItemRows = """
Ability: Faydown Cloak|Skill
Ability: Needle Strike|Skill
Ancestral Art: Silk Soar|Skill
Ancestral Art: Cling Grip|Skill
Ability: Drifter's Cloak|Skill
Ancestral Art: Swift Step|Skill
Ancestral Art: Clawline|Skill
Item: Quill|Skill
Ancestral Art: Needolin|Skill
Tool: Silkshot (Forge Daughter)|Tool
Tool: Silkshot (Twelfth Architect)|Tool
Tool: Silkshot (Original)|Tool
Tool: Volt Filament|Tool
Tool: Tacks|Tool
Tool: Pollip Pouch|Tool
Tool: Snare Setter|Tool
Tool: Wispfire Lantern|Tool
Tool: Memory Crystal|Tool
Tool: Voltvessels|Tool
Tool: Threefold Pin|Tool
Tool: Warding Bell|Tool
Tool: Rosary Cannon|Tool
Tool: Longpin|Tool
Tool: Sawtooth Circlet|Tool
Progressive Claw Mirror|Tool
Tool: Throwing Ring|Tool
Tool: Delver's Drill|Tool
Tool: Quick Sling|Tool
Tool: Spider Strings|Tool
Tool: Fractured Mask|Tool
Tool: Silkspeed Anklets|Tool
Tool: Weighted Belt|Tool
Tool: Multibinder|Tool
Tool: Reserve Bind|Tool
Tool: Injector Band|Tool
Tool: Barbed Bracelet|Tool
Tool: Sting Shard|Tool
Tool: Conchcutter|Tool
Tool: Pimpillo|Tool
Tool: Straight Pin|Tool
Tool: Cogfly|Tool
Progressive Curveclaw|Tool
Tool: Cogwork Wheel|Tool
Tool: Flea Brew|Tool
Tool: Magnetite Dice|Tool
Tool: Magnetite Brooch|Tool
Tool: Weavelight|Tool
Tool: Pin Badge|Tool
Tool: Needle Phial|Tool
Tool: Plasmium Phial|Tool
Tool: Shell Satchel|Tool
Tool: Dead Bug's Purse|Tool
Tool: Scuttlebrace|Tool
Tool: Thief's Mark|Tool
Tool: Compass|Tool
Tool: Snitch Pick|Tool
Tool: Flintslate|Tool
Tool: Wreath of Purity|Tool
Tool: Magma Bell|Tool
Tool: Ascendant's Grip|Tool
Tool: Longclaw|Tool
Tool: Shard Pendant|Tool
Tool: Spool Extender|Tool
Tool: Egg of Flealia|Tool
Silk Skill: Silkspear|Spell
Silk Skill: Cross Stitch|Spell
Silk Skill: Pale Nails|Spell
Silk Skill: Sharpdart|Spell
Silk Skill: Rune Rage|Spell
Silk Skill: Thread Storm|Spell
Crest: Witch|Crest
Crest: Beast|Crest
Crest: Architect|Crest
Crest: Shaman|Crest
Crest: Reaper|Crest
Crest: Wanderer|Crest
Crest: Hunter|Crest
Flea: The Marrow|Flea
Flea: Deep Docks (Bellway)|Flea
Flea: Deep Docks (Weaver Burial Spire)|Flea
Flea: Far Fields (Captured)|Flea
Flea: Hunter's March|Flea
Flea: Greymoor (Craw Lake)|Flea
Flea: Greymoor (Tower)|Flea
Flea: Shellwood|Flea
Flea: Pilgrim's Rest|Flea
Flea: Blasted Steps|Flea
Flea: Sinner's Road|Flea
Flea: Exhaust Organ|Flea
Flea: Bellhart|Flea
Flea: Wormways|Flea
Flea: The Slab (Cell)|Flea
Flea: Bilewater (Thieves)|Flea
Flea: Deep Docks (Mines)|Flea
Flea: Wisp Thicket|Flea
Flea: Bilehaven|Flea
Flea: Choral Chambers (Spa)|Flea
Flea: Sands of Karak|Flea
Flea: Mount Fay|Flea
Flea: Songclave|Flea
Flea: Choral Chambers (Walled Room)|Flea
Flea: Whispering Vaults|Flea
Flea: Underworks|Flea
Flea: The Slab (Bellway)|Flea
Crest Slot: Hunter (Red 1)|CrestSlot
Crest Slot: Hunter (Blue 1)|CrestSlot
Crest Slot: Hunter (Yellow 1)|CrestSlot
Crest Slot: Reaper (Red 1)|CrestSlot
Crest Slot: Reaper (Blue 1)|CrestSlot
Crest Slot: Reaper (Yellow 1)|CrestSlot
Crest Slot: Wanderer (Blue 1)|CrestSlot
Crest Slot: Wanderer (Blue 2)|CrestSlot
Crest Slot: Wanderer (Yellow 1)|CrestSlot
Crest Slot: Beast (Yellow 1)|CrestSlot
Crest Slot: Beast (Yellow 2)|CrestSlot
Crest Slot: Witch (Red 1)|CrestSlot
Crest Slot: Witch (Blue 1)|CrestSlot
Crest Slot: Witch (Blue 2)|CrestSlot
Crest Slot: Architect (Blue 1)|CrestSlot
Crest Slot: Architect (Yellow 1)|CrestSlot
Crest Slot: Architect (Yellow 2)|CrestSlot
Crest Slot: Architect (Blue 2)|CrestSlot
Crest Slot: Shaman (Blue 1)|CrestSlot
Crest Slot: Shaman (Blue 2)|CrestSlot
Mask Shard #1|MaskShard
Mask Shard #2|MaskShard
Mask Shard #3|MaskShard
Mask Shard #4|MaskShard
Mask Shard #5|MaskShard
Mask Shard #6|MaskShard
Mask Shard #7|MaskShard
Mask Shard #8|MaskShard
Mask Shard #9|MaskShard
Mask Shard #10|MaskShard
Mask Shard #11|MaskShard
Mask Shard #12|MaskShard
Mask Shard #13|MaskShard
Mask Shard #14|MaskShard
Mask Shard #15|MaskShard
Mask Shard #16|MaskShard
Mask Shard #17|MaskShard
Mask Shard #18|MaskShard
Mask Shard #19|MaskShard
Mask Shard #20|MaskShard
Spool Fragment #1|SpoolFragment
Spool Fragment #2|SpoolFragment
Spool Fragment #3|SpoolFragment
Spool Fragment #4|SpoolFragment
Spool Fragment #5|SpoolFragment
Spool Fragment #6|SpoolFragment
Spool Fragment #7|SpoolFragment
Spool Fragment #8|SpoolFragment
Spool Fragment #9|SpoolFragment
Spool Fragment #10|SpoolFragment
Spool Fragment #11|SpoolFragment
Spool Fragment #12|SpoolFragment
Spool Fragment #13|SpoolFragment
Spool Fragment #14|SpoolFragment
Spool Fragment #15|SpoolFragment
Spool Fragment #16|SpoolFragment
Spool Fragment #17|SpoolFragment
Spool Fragment #18|SpoolFragment
Bellway: Deep Docks|Bellway
Bellway: Far Fields|Bellway
Bellway: Greymoor|Bellway
Bellway: Bellhart|Bellway
Bellway: Blasted Steps|Bellway
Bellway: Grand Bellway|Bellway
Bellway: The Slab|Bellway
Bellway: Shellwood|Bellway
Bellway: Bilewater|Bellway
Bellway: Putrified Ducts|Bellway
Ventrica: Choral Chambers|Ventrica
Ventrica: Underworks|Ventrica
Ventrica: Grand Bellway|Ventrica
Ventrica: High Halls|Ventrica
Ventrica: Songclave|Ventrica
Ventrica: Memorium|Ventrica
Map: Mosslands|Map
Map: The Marrow|Map
Map: Deep Docks|Map
Map: Far Fields|Map
Map: Wormways|Map
Map: Hunter's March|Map
Map: Greymoor|Map
Map: Bellhart|Map
Map: Shellwood|Map
Map: Blasted Steps|Map
Map: Sinner's Road|Map
Map: Mount Fay|Map
Map: Sands of Karak|Map
Map: Bilewater|Map
Progressive Crafting Kit|Upgrade
Bench Pins|Pin
Ventrica Pins|Pin
Bellway Pins|Pin
Vendor Pins|Pin
Relic: Weaver Effigy (Keelal, Shellwood)|Relic
Relic: Psalm Cylinder (East Whispering Vaults)|Relic
Relic: Bone Scroll (Wisp Thicket)|Relic
Relic: Weaver Effigy (Camora, Moss Grotto)|Relic
Relic: Sacred Cylinder|Relic
Relic: Choral Commandment (Jubilana)|Relic
Relic: Psalm Cylinder (Underworks)|Relic
Relic: Rune Harp (High Halls)|Relic
Relic: Psalm Cylinder (Vaultkeeper Cardinius)|Relic
Relic: Psalm Cylinder (High Halls)|Relic
Relic: Choral Commandment (Western Whiteward)|Relic
Relic: Rune Harp (Weavenest Cindril)|Relic
Relic: Psalm Cylinder (Grindle)|Relic
Relic: Rune Harp (Weavenest Atla)|Relic
Relic: Bone Scroll (Underworks)|Relic
Relic: Bone Scroll (Far Fields)|Relic
Relic: Choral Commandment (Moss Grotto)|Relic
Relic: Choral Commandment (Eastern Whiteward)|Relic
Relic: Bone Scroll (Greymoor)|Relic
Relic: Weaver Effigy (Atla, The Slab)|Relic
Relic: Arcane Egg|Relic
Rosaries (60)|Currency
Shell Shards (80)|Currency
Stagger Trap|Trap
Rosary Spill Trap|Trap
Darkness Trap|Trap
Cursed Crest Trap|Trap
Muckmaggot Status Trap|Trap
Progressive Swift Step|Skill
Progressive Needle Upgrade|NeedleUpgrade
Pale Oil|NeedleUpgrade
Progressive Druid's Eyes|Upgrade
Progressive Compass|Skill
Frayed Rosary String|Resource
Rosary String|Resource
Rosary Necklace|Resource
Heavy Rosary Necklace|Resource
Pale Rosary Necklace|Resource
Shard Bundle|Resource
Beast Shard|Resource
Pristine Core|Resource
Rosaries (10)|Resource
Shell Shards (10)|Resource
Ruined Tool|Tool
Simple Key (Wormways)|Key
Simple Key (Deep Docks)|Key
Simple Key (Green Prince)|Key
Simple Key (Rosary Bank)|Key
Map: Weavenest Atla|Map
Map: Grand Gate|Map
Map: Underworks|Map
Map: Choral Chambers|Map
Map: Whispering Vaults|Map
Map: Whiteward|Map
Map: Cogwork Core|Map
Map: Memorium|Map
Map: High Halls|Map
Map: The Slab|Map
Map: Putrified Ducts|Map
Map: The Cradle|Map
Map: Verdania|Map
Map: The Abyss|Map
Architect's Melody|Melody
Conductor's Melody|Melody
Vaultkeeper's Melody|Melody
Elegy of the Deep|Melody
Beastling Call|Melody
Memory Locket|MemoryLocket
Craftmetal|Craftmetal
Mossberry|Mossberry
Silkeater|Silkeater
Key of Indolent|MajorKey
Key of Heretic|MajorKey
Key of Apostate|MajorKey
White Key|MajorKey
Surgeon's Key|MajorKey
Architect's Key|MajorKey
Craw Summons|MajorKey
Flea: Greymoor (Kratt)|Flea
Flea: Putrified Ducts (Vog)|Flea
Flea: Memorium (Huge Flea)|Flea
Naked Trap|Trap
Progressive Silkheart|SilkHeart
Progressive Tool Pouch|ToolPouch
Pollip Heart|PollipHeart
Growstone|Resource
Rosaries (8)|Resource
Rosaries (30)|Resource
Rosaries (70)|Resource
Rosaries (75)|Resource
Rosaries (84)|Resource
Rosaries (90)|Resource
Rosaries (105)|Resource
Rosaries (112)|Resource
Rosaries (115)|Resource
Rosaries (130)|Resource
Rosaries (155)|Resource
Shell Shards (35)|Resource
Bell: The Marrow|BellShrine
Bell: Deep Docks|BellShrine
Bell: Greymoor|BellShrine
Bell: Shellwood|BellShrine
Bell: Bellhart|BellShrine
""";
}
