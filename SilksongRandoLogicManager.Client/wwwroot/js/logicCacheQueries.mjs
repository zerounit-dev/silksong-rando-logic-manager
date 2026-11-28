// Final named-read extraction, called only after the owner's release/state/CAS
// admission. No storage lifetime, synchronization cells or business rules live here.
// Each scalar is read directly from SQLite into a named result, never a cell graph.
const quote = name => `"${name.replaceAll('"', '""')}"`;
const vocabulary = ['Predicates', 'Crests', 'Methods', 'Items', 'Countables', 'Souls'];
const children = ['Subrooms', 'RoomTransitions', 'SubroomConnections', 'CheckLocations', 'RequirementExplanations'];
const uuid = value => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);

export function extractNamedQuery(db, sqlite, request, current, observation = null, clock = () => performance.now()) {
    const c = sqlite.capi;
    function select(table, names, map, where = '', bind = [], ordered = true) {
        const started = observation ? clock() : 0;
        if (observation) observation.statements++;
        const order = !ordered ? '' : ['Rooms', 'RoomGroups'].includes(table) ? 'ORDER BY SortOrder,FriendlyName,Id'
            : [...children, ...vocabulary.map(x => `Requirement${x}`)].includes(table) ? 'ORDER BY SortOrder,Id' : '';
        const statement = db.prepare(`SELECT ${names.map(quote).join(',')} FROM ${quote(table)} ${where} ${order}`);
        try {
            if (bind.length) statement.bind(bind);
            const result = [];
            // Fixed source columns, resolved once per statement. This accessor never
            // leaves the worker or stores a second representation of a source row.
            const indexes = new Map(names.map((name, index) => [name, index]));
            function index(name) {
                const i = indexes.get(name);
                if (i === undefined) throw new Error('Missing named SQLite source column.');
                if (observation) observation.sourceValues = (observation.sourceValues ?? 0) + 1;
                return i;
            }
            function type(i) { return c.sqlite3_column_type(statement.pointer, i); }
            function invalid() { throw new Error('Invalid persisted named SQLite value.'); }
            function text(name, nullable = false) {
                const i = index(name), t = type(i);
                if (nullable && t === c.SQLITE_NULL) return null;
                if (t !== c.SQLITE_TEXT) return invalid();
                return statement.getString(i);
            }
            function integer(name, nullable = false) {
                const i = index(name), t = type(i);
                if (nullable && t === c.SQLITE_NULL) return null;
                if (t !== c.SQLITE_INTEGER) return invalid();
                return c.sqlite3_column_int64(statement.pointer, i);
            }
            const r = {
                text, integer,
                int(name) { const x = integer(name); if (x < -2147483648n || x > 2147483647n) return invalid(); return Number(x); },
                bool(name, nullable = false) { const x = integer(name, nullable); if (x === null) return null;
                    if (x !== 0n && x !== 1n) return invalid(); return x === 1n; },
                real(name, nullable = false) {
                    const i = index(name), t = type(i);
                    if (nullable && t === c.SQLITE_NULL) return null;
                    if (t === c.SQLITE_INTEGER) return c.sqlite3_column_int64(statement.pointer, i).toString();
                    if (t !== c.SQLITE_FLOAT) return invalid();
                    // Infinities stay strings; JSON must never replace them with null.
                    return statement.getFloat(i).toString();
                }
            };
            while (statement.step()) {
                if (observation) { observation.rows++; observation.cells += names.length; }
                result.push(map(r));
                if (observation) {
                    observation.namedRows = (observation.namedRows ?? 0) + 1;
                }
            }
            return result;
        } finally {
            try { statement.finalize(); }
            finally { if (observation) observation.extractionMs += clock() - started; }
        }
    }
    const groupNames = ['Id','FriendlyName','SortOrder','ZoneReferenceText','ResolvedMapZoneId','IsVirtual','UpdatedUtc'];
    const group = r => ({ id: r.text('Id'), friendlyName: r.text('FriendlyName'), sortOrder: r.int('SortOrder'),
        zoneReferenceText: r.text('ZoneReferenceText', true), resolvedMapZoneId: r.text('ResolvedMapZoneId', true),
        isVirtual: r.bool('IsVirtual'), updatedUtc: r.text('UpdatedUtc') });
    const groups = (where = '', bind = []) => select('RoomGroups', groupNames, group, where, bind);
    function definitions() {
        const result = {};
        for (const kind of vocabulary) {
            const predicate = kind === 'Predicates', output = predicate ? 'OutputSyntax' : 'OutputValue';
            result[kind.toLowerCase()] = select(`Requirement${kind}`,
                ['Id','Name','Category','SortOrder','Aliases', ...(predicate ? ['InputSyntax'] : []), output,'Notes'],
                r => ({ id: r.text('Id'), name: r.text('Name'), category: r.text('Category', true), sortOrder: r.int('SortOrder'),
                    aliases: r.text('Aliases'), inputSyntax: predicate ? r.text('InputSyntax') : null,
                    output: r.text(output), notes: r.text('Notes') }));
        }
        return result;
    }
    const inventory = ordered => select('Rooms', ['Id','CacheFlagUtc','CompletionStatus'], r => ({ id: r.text('Id'),
        cacheFlagUtc: r.integer('CacheFlagUtc').toString(), completionStatus: r.int('CompletionStatus') }), '', [], ordered);
    const identity = r => ({ entityId: r.text('Id'), isArchived: r.bool('IsArchived') });
    const row = r => ({ entityId: r.text('Id'), updatedUtc: r.text('UpdatedUtc'), isArchived: r.bool('IsArchived') });
    const metadataNames = ['InGameId','InGamePositionX','InGamePositionY','InGamePositionZ','LocalPositionX','LocalPositionY','LocalPositionZ','AnnotationSceneUnitX','AnnotationSceneUnitY'];
    const metadata = r => ({ inGameId: r.text('InGameId', true), inGamePositionX: r.real('InGamePositionX', true),
        inGamePositionY: r.real('InGamePositionY', true), inGamePositionZ: r.real('InGamePositionZ', true),
        localPositionX: r.real('LocalPositionX', true), localPositionY: r.real('LocalPositionY', true), localPositionZ: r.real('LocalPositionZ', true),
        annotationSceneUnitX: r.real('AnnotationSceneUnitX', true), annotationSceneUnitY: r.real('AnnotationSceneUnitY', true) });
    const imageNames = ['SceneMinX','SceneMinY','SceneMaxX','SceneMaxY','SceneImagePixelsPerUnitX','SceneImagePixelsPerUnitY',
        'SceneImagePixelOffsetX','SceneImagePixelOffsetY','UseSceneImage','UseUploadedSceneImage','IsSceneImageStale','SceneImageOriginalHash','SceneImageCropHash'];
    switch (request.name) {
        case 'inventory': return { state: current, rooms: inventory(false) };
        case 'vocabulary': return { state: current, vocabulary: definitions() };
        case 'catalogue': {
            if (!Array.isArray(request.knownRooms)) throw new Error('Missing retained Room observations.');
            const known = new Map();
            for (const room of request.knownRooms) {
                if (!uuid(room.id) || typeof room.cacheFlagUtc !== 'string' || !/^[0-9]+$/.test(room.cacheFlagUtc) ||
                    known.has(room.id.toUpperCase())) throw new Error('Invalid retained Room observation.');
                known.set(room.id.toUpperCase(), room.cacheFlagUtc);
            }
            const all = inventory(true);
            const changed = all.filter(x => known.get(x.id.toUpperCase()) !== x.cacheFlagUtc).map(x => x.id);
            function selected(table, names, map) {
                const result = [], key = table === 'Rooms' ? 'Id' : 'RoomId';
                for (let at = 0; at < changed.length; at += 100) {
                    const batch = changed.slice(at, at + 100);
                    const part = select(table, names, map, `WHERE ${key} IN (${batch.map(() => '?').join(',')})`, batch);
                    for (const item of part) result.push(item);
                }
                return result;
            }
            const requirements = (r, archived) => archived ? null : r.text('Requirements');
            return { state: current, inventory: all, groups: groups(), vocabulary: definitions(),
                rooms: selected('Rooms', ['Id','CacheFlagUtc','IsArchived','FriendlyName','ReferenceId','InGameId','RoomGroupId','SortOrder','CompletionStatus',
                    'UseSceneImage','UseUploadedSceneImage','IsSceneImageStale','SceneImagePixelsPerUnitX','SceneImagePixelsPerUnitY'], r => {
                    const id = r.text('Id');
                    return { observation: { roomId: id, cacheFlagUtc: r.integer('CacheFlagUtc').toString() },
                        identity: { entityId: id, isArchived: r.bool('IsArchived') },
                        values: { friendlyName: r.text('FriendlyName'), referenceId: r.text('ReferenceId'), inGameId: r.text('InGameId', true) },
                        roomGroupId: r.text('RoomGroupId', true), sortOrder: r.int('SortOrder'), completionStatus: r.int('CompletionStatus'),
                        sceneImage: { useSceneImage: r.bool('UseSceneImage'), useUploadedSceneImage: r.bool('UseUploadedSceneImage'), stale: r.bool('IsSceneImageStale'),
                            pixelsPerUnitX: r.real('SceneImagePixelsPerUnitX', true), pixelsPerUnitY: r.real('SceneImagePixelsPerUnitY', true) } };
                }),
                transitions: selected('RoomTransitions', ['Id','RoomId','IsArchived','Alias','DestinationRoomReferenceText','DestinationTransitionAliasText',
                    'ResolvedDestinationRoomId','ResolvedDestinationTransitionId','Requirements'], r => {
                    const observation = identity(r);
                    return { roomId: r.text('RoomId'), facts: { observation, values: { alias: r.text('Alias'),
                        destinationRoomReferenceText: r.text('DestinationRoomReferenceText', true), destinationTransitionAliasText: r.text('DestinationTransitionAliasText', true) },
                        resolvedDestinationRoomId: r.text('ResolvedDestinationRoomId', true), resolvedDestinationTransitionId: r.text('ResolvedDestinationTransitionId', true) },
                        requirements: requirements(r, observation.isArchived) };
                }),
                connections: selected('SubroomConnections', ['Id','RoomId','IsArchived','Requirements'], r => {
                    const observation = identity(r);
                    return { roomId: r.text('RoomId'), identity: observation, requirements: requirements(r, observation.isArchived) };
                }),
                checks: selected('CheckLocations', ['Id','RoomId','IsArchived','FriendlyName','Requirements'], r => {
                    const observation = identity(r);
                    return { roomId: r.text('RoomId'), facts: { observation, values: { friendlyName: r.text('FriendlyName') } },
                        requirements: requirements(r, observation.isArchived) };
                }) };
        }
        case 'maps': {
            const boundsNames = ['MapUnitMinX','MapUnitMinY','MapUnitMaxX','MapUnitMaxY'];
            const bounds = r => ({ minX: r.real('MapUnitMinX', true), minY: r.real('MapUnitMinY', true),
                maxX: r.real('MapUnitMaxX', true), maxY: r.real('MapUnitMaxY', true) });
            return { state: current,
                maps: select('Maps', ['Id','SortOrder',...boundsNames], r => ({ id: r.text('Id'), sortOrder: r.int('SortOrder'), ...bounds(r) })),
                zones: select('MapZones', ['Id','MapId','InGameId',...boundsNames], r => ({ id: r.text('Id'), mapId: r.text('MapId'), inGameId: r.text('InGameId'), ...bounds(r) })),
                scenes: select('MapScenes', ['Id','MapZoneId','InGameId','RoomReferenceText','ResolvedRoomId'], r => ({ id: r.text('Id'), mapZoneId: r.text('MapZoneId'),
                    inGameId: r.text('InGameId'), roomReferenceText: r.text('RoomReferenceText', true), resolvedRoomId: r.text('ResolvedRoomId', true) })),
                chunks: select('MapChunks', ['Id','MapSceneId','CacheIndex',...boundsNames,'MapUnitZ'], r => ({ id: r.text('Id'), mapSceneId: r.text('MapSceneId'),
                    cacheIndex: r.int('CacheIndex'), ...bounds(r), z: r.real('MapUnitZ', true) })),
                overlays: select('MapOverlays', ['Id','MapId','ImageAssetKey','SortOrder','ScaleXPercent','ScaleYPercent','LeftOffsetPercent','BottomOffsetPercent'],
                    r => ({ id: r.text('Id'), mapId: r.text('MapId'), imageAssetKey: r.text('ImageAssetKey'), sortOrder: r.int('SortOrder'),
                        scaleXPercent: r.real('ScaleXPercent'), scaleYPercent: r.real('ScaleYPercent'),
                        leftOffsetPercent: r.real('LeftOffsetPercent'), bottomOffsetPercent: r.real('BottomOffsetPercent') })) };
        }
        case 'room': {
            if (!uuid(request.roomId)) throw new Error('Invalid local Room identity.');
            const bind = [request.roomId];
            const rooms = select('Rooms', ['Id','CacheFlagUtc','UpdatedUtc','IsArchived','FriendlyName','ReferenceId','InGameId','Contributors','Comments',
                'RoomGroupId','SortOrder','CompletionStatus',...imageNames], r => {
                const observed = row(r);
                return { observation: { roomId: observed.entityId, cacheFlagUtc: r.integer('CacheFlagUtc').toString() }, row: observed,
                    values: { friendlyName: r.text('FriendlyName'), referenceId: r.text('ReferenceId'), inGameId: r.text('InGameId', true),
                        contributors: r.text('Contributors', true), comments: r.text('Comments', true) }, roomGroupId: r.text('RoomGroupId', true),
                    sortOrder: r.int('SortOrder'), completionStatus: r.int('CompletionStatus'),
                    sceneImage: { minX: r.real('SceneMinX'), minY: r.real('SceneMinY'), maxX: r.real('SceneMaxX', true), maxY: r.real('SceneMaxY', true),
                        pixelsPerUnitX: r.real('SceneImagePixelsPerUnitX', true), pixelsPerUnitY: r.real('SceneImagePixelsPerUnitY', true),
                        pixelOffsetX: r.real('SceneImagePixelOffsetX', true), pixelOffsetY: r.real('SceneImagePixelOffsetY', true),
                        useSceneImage: r.bool('UseSceneImage'), useUploadedSceneImage: r.bool('UseUploadedSceneImage'), stale: r.bool('IsSceneImageStale'),
                        originalHash: r.text('SceneImageOriginalHash', true), cropHash: r.text('SceneImageCropHash', true), updatedUtc: observed.updatedUtc } };
            }, 'WHERE Id=? COLLATE NOCASE', bind);
            const common = ['Id','RoomId','UpdatedUtc','IsArchived','SortOrder'];
            const child = (table, names, map) => select(table, [...common,...names], r => ({ roomId: r.text('RoomId'), snapshot: map(r) }), 'WHERE RoomId=? COLLATE NOCASE', bind);
            // Missing Room still validates returned child identity/ownership in C#,
            // but must not decode unused child bodies or group/document context.
            if (!rooms.length) {
                const absent = table => select(table, ['Id','RoomId'], r => ({ id: r.text('Id'), roomId: r.text('RoomId') }), 'WHERE RoomId=? COLLATE NOCASE', bind);
                return { state: current, rooms, subrooms: absent('Subrooms'), transitions: absent('RoomTransitions'),
                    connections: absent('SubroomConnections'), checks: absent('CheckLocations'), explanations: absent('RequirementExplanations') };
            }
            return { state: current, rooms,
                explanations: child('RequirementExplanations', ['FriendlyName','ReferenceId','Link','Notes'], r => ({
                    observation: row(r), sortOrder: r.int('SortOrder'), values: {
                        friendlyName: r.text('FriendlyName'), referenceId: r.text('ReferenceId'), link: r.text('Link'), notes: r.text('Notes') } })),
                subrooms: child('Subrooms', ['FriendlyName','ReferenceId','Notes','SceneUnitX','SceneUnitY','SceneUnitWidth','SceneUnitHeight','EnableAnnotation'], r => ({
                    observation: row(r), sortOrder: r.int('SortOrder'), values: { friendlyName: r.text('FriendlyName'), referenceId: r.text('ReferenceId'), notes: r.text('Notes', true) },
                    geometry: { x: r.real('SceneUnitX', true), y: r.real('SceneUnitY', true), width: r.real('SceneUnitWidth', true), height: r.real('SceneUnitHeight', true), enableAnnotation: r.bool('EnableAnnotation') } })),
                transitions: child('RoomTransitions', ['Alias','FriendlyName','SourceSubroomReferenceText','DestinationRoomReferenceText','DestinationTransitionAliasText',
                    'Requirements','Notes','IsTodo','IsVerified','IsUnsafeArrival',...metadataNames,'EnableAnnotation','RequirementsParseSucceeded','ResolvedSourceSubroomId','ResolvedDestinationRoomId','ResolvedDestinationTransitionId'], r => ({
                    observation: row(r), sortOrder: r.int('SortOrder'), values: { alias: r.text('Alias'), friendlyName: r.text('FriendlyName'), sourceSubroomReferenceText: r.text('SourceSubroomReferenceText', true),
                        destinationRoomReferenceText: r.text('DestinationRoomReferenceText', true), destinationTransitionAliasText: r.text('DestinationTransitionAliasText', true),
                        requirements: r.text('Requirements'), notes: r.text('Notes'), isTodo: r.bool('IsTodo'), isVerified: r.bool('IsVerified', true), isUnsafeArrival: r.bool('IsUnsafeArrival') },
                    metadata: metadata(r), enableAnnotation: r.bool('EnableAnnotation'), requirementsParseSucceeded: r.bool('RequirementsParseSucceeded', true),
                    resolvedSourceSubroomId: r.text('ResolvedSourceSubroomId', true), resolvedDestinationRoomId: r.text('ResolvedDestinationRoomId', true), resolvedDestinationTransitionId: r.text('ResolvedDestinationTransitionId', true) })),
                connections: child('SubroomConnections', ['Alias','FriendlyName','SourceSubroomReferenceText','DestinationSubroomReferenceText','Requirements','Notes','IsTodo','IsVerified',
                    'SceneUnitX','SceneUnitY','EnableAnnotation','RequirementsParseSucceeded','ResolvedSourceSubroomId','ResolvedDestinationSubroomId'], r => ({
                    observation: row(r), sortOrder: r.int('SortOrder'), values: { alias: r.text('Alias'), friendlyName: r.text('FriendlyName'), sourceSubroomReferenceText: r.text('SourceSubroomReferenceText'),
                        destinationSubroomReferenceText: r.text('DestinationSubroomReferenceText'), requirements: r.text('Requirements'), notes: r.text('Notes'), isTodo: r.bool('IsTodo'), isVerified: r.bool('IsVerified', true) },
                    sceneUnitX: r.real('SceneUnitX', true), sceneUnitY: r.real('SceneUnitY', true), enableAnnotation: r.bool('EnableAnnotation'), requirementsParseSucceeded: r.bool('RequirementsParseSucceeded', true),
                    resolvedSourceSubroomId: r.text('ResolvedSourceSubroomId', true), resolvedDestinationSubroomId: r.text('ResolvedDestinationSubroomId', true) })),
                checks: child('CheckLocations', ['FriendlyName','SubroomReferenceText','Requirements','Notes','LocationType','IsTodo','IsVerified',...metadataNames,
                    'EnableAnnotation','RequirementsParseSucceeded','ResolvedSubroomId'], r => ({ observation: row(r), sortOrder: r.int('SortOrder'),
                    values: { friendlyName: r.text('FriendlyName'), subroomReferenceText: r.text('SubroomReferenceText', true), requirements: r.text('Requirements'), notes: r.text('Notes'),
                        locationType: r.text('LocationType', true), isTodo: r.bool('IsTodo'), isVerified: r.bool('IsVerified', true) }, metadata: metadata(r),
                    enableAnnotation: r.bool('EnableAnnotation'), requirementsParseSucceeded: r.bool('RequirementsParseSucceeded', true), resolvedSubroomId: r.text('ResolvedSubroomId', true) })),
                groups: groups('WHERE Id IN (SELECT RoomGroupId FROM Rooms WHERE Id=? COLLATE NOCASE)', bind),
                roomOrder: select('Rooms', ['Id'], r => r.text('Id')),
                groupOrder: select('RoomGroups', ['Id'], r => r.text('Id')) };
        }
        default: throw new Error('Unknown named confirmed-cache query.');
    }
}
