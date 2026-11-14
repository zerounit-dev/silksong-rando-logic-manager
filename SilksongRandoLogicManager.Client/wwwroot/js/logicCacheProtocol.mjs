// Application-owned protocol, not vendor coordination code. Only confirmed logic
// facts cross this boundary; credentials, drafts and business commands never do.
export const ownerLock = 'silksong-logic-cache-owner';
export const databasePath = '/logic.db';
export const metadataTable = '__LogicCache';
export const vocabularyTables = Object.freeze(['RequirementPredicates', 'RequirementItems',
    'RequirementCrests', 'RequirementMethods', 'RequirementCountables', 'RequirementSouls']);
// Parent-first for inserts, reversed for transaction-private deletion staging.
// Self-referencing transition edges are checked after the complete final graph exists.
export const tables = Object.freeze(['Maps', 'MapZones', 'RoomGroups', 'Rooms', 'Subrooms',
    'RoomTransitions', 'SubroomConnections', 'CheckLocations', 'MapScenes', 'MapChunks',
    'MapOverlays', ...vocabularyTables]);
const audit = 'Id CreatedUtc UpdatedUtc';
const archive = `${audit} IsArchived ArchivedUtc`;
const position = 'InGamePositionX InGamePositionY InGamePositionZ LocalPositionX LocalPositionY LocalPositionZ AnnotationSceneUnitX AnnotationSceneUnitY';
const logic = 'Requirements RequirementsParseSucceeded Notes SortOrder IsTodo IsVerified';
const bounds = 'MapUnitMinX MapUnitMinY MapUnitMaxX MapUnitMaxY';
const value = 'Id Name Category OutputValue Aliases Notes SortOrder';
// Exact persisted field inventory; order is obtained from the copied SQLite schema,
// not guessed from EF property ordering or JSON projection ordering.
export const fields = Object.freeze(Object.fromEntries(Object.entries({
    RoomGroups: `${audit} FriendlyName ZoneReferenceText ResolvedMapZoneId IsVirtual SortOrder`,
    Rooms: `${archive} CacheFlagUtc CompletionStatus RoomGroupId ReferenceId FriendlyName InGameId Contributors Comments SceneMinX SceneMinY SceneMaxX SceneMaxY SceneImagePixelsPerUnitX SceneImagePixelsPerUnitY SceneImagePixelOffsetX SceneImagePixelOffsetY UseSceneImage UseUploadedSceneImage SceneImageOriginalHash SceneImageCropHash IsSceneImageStale SortOrder`,
    Subrooms: `${archive} RoomId ReferenceId FriendlyName Notes SceneUnitX SceneUnitY SceneUnitWidth SceneUnitHeight EnableAnnotation SortOrder`,
    RoomTransitions: `${archive} RoomId Alias FriendlyName InGameId IsUnsafeArrival ${position} EnableAnnotation SourceSubroomReferenceText DestinationRoomReferenceText DestinationTransitionAliasText ResolvedSourceSubroomId ResolvedDestinationRoomId ResolvedDestinationTransitionId ${logic}`,
    SubroomConnections: `${archive} RoomId Alias FriendlyName SourceSubroomReferenceText DestinationSubroomReferenceText EnableAnnotation SceneUnitX SceneUnitY ResolvedSourceSubroomId ResolvedDestinationSubroomId ${logic}`,
    CheckLocations: `${archive} RoomId FriendlyName InGameId ${position} SubroomReferenceText LocationType EnableAnnotation ResolvedSubroomId ${logic}`,
    Maps: `Id InGameId FriendlyName SortOrder ${bounds}`,
    MapZones: `Id MapId InGameId FriendlyName ${bounds}`,
    MapScenes: 'Id MapZoneId InGameId FriendlyName RoomReferenceText ResolvedRoomId',
    MapChunks: `Id MapSceneId CacheIndex InitialState ${bounds} MapUnitZ`,
    MapOverlays: 'Id MapId FriendlyName ImageAssetKey ScaleXPercent ScaleYPercent LeftOffsetPercent BottomOffsetPercent SortOrder',
    RequirementPredicates: 'Id Name Category InputSyntax OutputSyntax Aliases Notes SortOrder',
    RequirementItems: value, RequirementCrests: value, RequirementMethods: value,
    RequirementCountables: value, RequirementSouls: value
}).map(([table, columns]) => [table, Object.freeze(columns.split(' '))])));

export const uuid = value => typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
export function checkpoint(value) {
    const utc = x => typeof x === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$/.test(x);
    if (!value || value.format !== 1 || !uuid(value.runId) || !utc(value.startedUtc) ||
        !utc(value.throughUtc) || !/^[0-9a-f]{64}$/.test(value.schema))
        throw new Error('Unusable confirmed-cache checkpoint.');
    return value;
}
export const sameCheckpoint = (a, b) => !!a && !!b && a.format === b.format && a.runId === b.runId &&
    a.startedUtc === b.startedUtc && a.throughUtc === b.throughUtc && a.schema === b.schema;
export const quote = name => '"' + name.replaceAll('"', '""') + '"';

export function bindCell(cell) {
    if (!cell || typeof cell.type !== 'string') throw new Error('Missing typed SQLite cell.');
    const value = cell.value;
    switch (cell.type) {
        case 'Null': if (value === null) return null; break;
        case 'Text': if (typeof value === 'string') return value; break;
        case 'Integer':
            if (typeof value === 'string' && /^-?(0|[1-9][0-9]*)$/.test(value)) {
                const integer = BigInt(value);
                if (integer >= -9223372036854775808n && integer <= 9223372036854775807n) return integer;
            }
            break;
        case 'Real':
            if (typeof value === 'string' && /^-?(?:[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|Infinity)$/.test(value))
                return Number(value);
            break;
        case 'Blob':
            if (typeof value === 'string') return Uint8Array.from(atob(value), x => x.charCodeAt(0));
            break;
    }
    throw new Error('Invalid typed SQLite cell.');
}
