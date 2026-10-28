using System.Globalization;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components;

// Display only. Never replace the raw comparison sides used for freshness/copying.
internal static class DistributedEvidencePresentation
{
    public static readonly IReadOnlyList<string> RoomProse = ["id", "friendlyName", "referenceId", "inGameId", "contributors", "comments", "roomGroupId", "sortOrder", "isArchived", "archivedUtc", "createdUtc", "updatedUtc"];

    // Display keys only. The underlying comparison inventory remains complete and
    // field-shaped; these groups never participate in choices/counts/freshness.
    public static IEnumerable<string> Columns(IEnumerable<string> fields) =>
        fields.OrderBy(x => x == "id" ? 0 : 1).Where(x => x is not ("localPositionY" or "localPositionZ" or "useUploadedSceneImage" or "archivedUtc"));

    public static IReadOnlyList<string> Constituents(string name) => name switch
    {
        "localPositionX" => ["localPositionX", "localPositionY", "localPositionZ"],
        "useSceneImage" => ["useSceneImage", "useUploadedSceneImage"],
        "isArchived" => ["isArchived", "archivedUtc"],
        _ => [name]
    };

    public static DistributedComparisonEvidence Evidence(string name, IReadOnlyList<DistributedComparisonValue> fields)
    {
        var evidence = Constituents(name).Where(x => x != "archivedUtc").Select(x => fields.Single(f => f.FieldName == x).Evidence).ToArray();
        return evidence.Contains(DistributedComparisonEvidence.Changed) ? DistributedComparisonEvidence.Changed
            : evidence.Contains(DistributedComparisonEvidence.Current) ? DistributedComparisonEvidence.Current
            : evidence.Contains(DistributedComparisonEvidence.Incoming) ? DistributedComparisonEvidence.Incoming
            : DistributedComparisonEvidence.Neutral;
    }

    public static bool IsIcon(string name) => name is "id" or "localPositionX" or "useSceneImage" or "sceneImageOriginalHash" or "sceneImageCropHash" or "isArchived" or "enableAnnotation" or "createdUtc";

    public static bool IsNoWrapTableValue(string name) =>
        name is "locationType" or "friendlyName" or "subroomReferenceText" or "sourceSubroomReferenceText" or "destinationSubroomReferenceText"
        or "alias" or "destinationTransitionAliasText" or "destinationRoomReferenceText" or "referenceId"
        or "sortOrder" or "isTodo" or "isVerified" or "isUnsafeArrival"
        || Decimals(name) is not null;

    public static string? Raw(string name, IReadOnlyList<DistributedComparisonValue> fields, bool incoming)
    {
        var field = fields.Single(x => x.FieldName == name);
        return incoming ? field.IncomingValue : field.CurrentValue;
    }

    public static string State(string name, IReadOnlyList<DistributedComparisonValue> fields, bool incoming) => name switch
    {
        "useSceneImage" => Raw(name, fields, incoming) == "false" ? "None" : Raw("useUploadedSceneImage", fields, incoming) == "true" ? "Uploaded image" : "ScriptersWar crop",
        "isArchived" => Raw(name, fields, incoming) == "true" ? "Archived" : "Active",
        "enableAnnotation" => Raw(name, fields, incoming) == "true" ? "Annotation enabled" : "Annotation disabled",
        _ => Heading(name)
    };

    public static string Icon(string name, IReadOnlyList<DistributedComparisonValue> fields, bool incoming) => name switch
    {
        "id" => "fa-fingerprint",
        "localPositionX" => "fa-location-dot",
        "useSceneImage" => Raw(name, fields, incoming) == "false" ? "fa-ban" : Raw("useUploadedSceneImage", fields, incoming) == "true" ? "fa-image" : "fa-map",
        "sceneImageOriginalHash" or "sceneImageCropHash" => Evidence(name, fields) == DistributedComparisonEvidence.Changed ? "fa-not-equal" : Evidence(name, fields) == DistributedComparisonEvidence.Neutral ? "fa-equals" : "fa-fingerprint",
        "isArchived" => Raw(name, fields, incoming) == "true" ? "fa-box-archive" : "fa-box-open",
        "enableAnnotation" => Raw(name, fields, incoming) == "true" ? "fa-eye" : "fa-eye-slash",
        "createdUtc" => "fa-calendar-plus",
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public static string Details(string name, IReadOnlyList<DistributedComparisonValue> fields, bool incoming)
    {
        var evidence = Evidence(name, fields);
        string Side(bool side) => $"{(side ? "Incoming" : "Current")}: {State(name, fields, side)}; " +
            string.Join("; ", Constituents(name).Select(field =>
            {
                var raw = Raw(field, fields, side);
                var value = raw ?? "null";
                if (field is "createdUtc" or "archivedUtc" && raw is not null)
                    value = $"{Format(field, raw)} (raw: {raw})";
                return $"{field}: {value}";
            }));
        var status = evidence switch { DistributedComparisonEvidence.Changed => "changed", DistributedComparisonEvidence.Neutral => "equal evidence", DistributedComparisonEvidence.Current => "current only", _ => "incoming only" };
        // Both real sides, including tolerated values and neutral timestamps, are
        // available. Never invent an absent side for new/one-sided records.
        return $"{Heading(name)} — {status}; displayed {(incoming ? "incoming" : "current")}.\n" +
            (evidence == DistributedComparisonEvidence.Current ? Side(false) : evidence == DistributedComparisonEvidence.Incoming ? Side(true) : $"{Side(false)}\n{Side(true)}");
    }

    private static int? Decimals(string field) => field switch
    {
        "sceneImagePixelsPerUnitX" or "sceneImagePixelsPerUnitY" => 4,
        "inGamePositionX" or "inGamePositionY" or "inGamePositionZ" or "localPositionX" or "localPositionY" or "localPositionZ" => 3,
        "sceneMinX" or "sceneMinY" or "sceneMaxX" or "sceneMaxY" or "sceneUnitX" or "sceneUnitY" or "sceneUnitWidth" or "sceneUnitHeight" or "annotationSceneUnitX" or "annotationSceneUnitY" or "sceneImagePixelOffsetX" or "sceneImagePixelOffsetY" => 2,
        _ => null
    };

    public static string? Format(string field, string? raw)
    {
        if (raw is null) return null;
        if (Decimals(field) is { } decimals && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
        if (field is "archivedUtc" or "createdUtc" or "updatedUtc")
            // SQLite projections may have Unspecified Kind; the schema still
            // defines these ticks as UTC, never the server's local time zone.
            return DateTimeOffset.Parse(JsonSerializer.Deserialize<string>(raw)!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return raw;
    }

    public static string Heading(string field) => field switch
    {
        "friendlyName" => "Name", "referenceId" => "Reference", "inGameId" => "Game ID",
        "roomGroupId" => "Group ID", "id" => "ID", "alias" => "Alias",
        "sourceSubroomReferenceText" => "Source subroom", "destinationSubroomReferenceText" => "Destination subroom",
        "destinationRoomReferenceText" => "Dest. room", "destinationTransitionAliasText" => "Dest. alias",
        "subroomReferenceText" => "Subroom", "locationType" => "Type",
        "isUnsafeArrival" => "Unsafe arrival", "isTodo" => "TODO", "isVerified" => "Verified",
        "sortOrder" => "Sort order", "isArchived" => "Archived", "enableAnnotation" => "Annotation enabled",
        "sceneMinX" => "Min X", "sceneMinY" => "Min Y", "sceneMaxX" => "Max X", "sceneMaxY" => "Max Y",
        "sceneUnitX" => "Scene X", "sceneUnitY" => "Scene Y",
        "annotationSceneUnitX" => "Annotation X", "annotationSceneUnitY" => "Annotation Y",
        "sceneUnitWidth" => "Width", "sceneUnitHeight" => "Height",
        "inGamePositionX" => "Game X", "inGamePositionY" => "Game Y", "inGamePositionZ" => "Game Z",
        "localPositionX" => "Local XYZ", "localPositionY" => "Local Y", "localPositionZ" => "Local Z",
        "sceneImagePixelsPerUnitX" => "Scale X", "sceneImagePixelsPerUnitY" => "Scale Y",
        "sceneImagePixelOffsetX" => "Offset X", "sceneImagePixelOffsetY" => "Offset Y", "useSceneImage" => "Image source",
        "useUploadedSceneImage" => "Uploaded", "sceneImageOriginalHash" => "Original hash", "sceneImageCropHash" => "Crop hash",
        "archivedUtc" => "Archived UTC", "createdUtc" => "Created", "updatedUtc" => "Updated UTC",
        "requirements" => "Requirements", "notes" => "Notes", "contributors" => "Contributors", "comments" => "Comments",
        _ => field
    };

}
