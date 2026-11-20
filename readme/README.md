# Silksong Rando Logic Manager

Silksong Rando Logic Manager is a local tool for documenting Silksong rooms and
the logic used to traverse them. It organizes rooms, exits, subrooms,
connections, checks, requirements, notes, and verification status. The normal
way to begin a room is by importing information collected by the Scene Dumper.

## Start the App

Read [authentication setup and recovery](AUTHENTICATION.md) before sharing the
application. Portable packages deliberately use local **bypass** mode; change to
required mode before exposing through a tunnel. Source checkouts contain actual nonsecret
`Config/appsettings.json` and Development defaults; retain the installation's
existing explicit mode, while source publication independently generates required-mode
defaults. Environment/CLI can override the file; no missing-mode default is inferred.
Keep credentials outside tracked settings using env/CLI or an external config locator.
See the guide for native base/environment precedence,
custom roots, transition and build/publication exclusion declarations.

The current V3 interface is at `/` (normally `http://localhost:5000/`). The retained
server interface is at `/v2/`; its room, catalogue and scene pages are respectively
`/v2/rooms/{id}`, `/v2/requirements` and `/v2/scene-import`. The authoritative room
import and map-manifest tools remain `/tools/room-import` and `/tools/map-manifest`.
The former `/v3/` UI URLs are removed without redirects. API, image and download
URLs are unchanged.

1. Extract the release archive into a folder. On Windows, extract the
   `win-x64` ZIP. On mainstream glibc Linux, extract the `linux-x64` tarball;
   on an Apple Silicon Mac (including M1), extract the `osx-arm64` tarball:
   `tar -xzf silksong-rando-logic-manager-<platform>.tar.gz`.
2. Run `Launch Silksong Rando Logic Manager.cmd` on Windows, or from a terminal
   run `./Launch\ Silksong\ Rando\ Logic\ Manager.sh` on Linux or macOS.
3. Wait for the tool to open `http://localhost:5000` in your default browser.
4. Keep the application running while you use the tool. Closing the browser tab
   does not close the application.

The tool defaults to `Storage` for mutable state and `Config/appsettings.json`
for private writable operator settings, inside `Silksong Rando Logic Manager`.
Before upgrading or a major import, arrange consistent private backups as described
in the authentication guide (offline copies require operator-controlled shutdown).
When upgrading, retain **both Config and Storage**, or their configured locations;
replace only application files, never retained settings/state with first-install
bypass settings or a public seed.

Source checkouts now also keep their DBs/backups and scene assets in Storage, with
C# definitions/migrations left in Data. The build hook consistently backs up the source
Storage primary DB to Storage/backups, not accounts/keys or external selected roots.
Linux
   releases target mainstream glibc distributions; Alpine Linux is not supported.
   The Mac release targets Apple Silicon only and is unsigned, so macOS may
   require you to approve it in Privacy & Security after the first launch attempt.

Published startup HTML is transformed by the .NET SDK for the same candidate, including
all six entry scripts, imports and preload metadata. Portable packaging checks the final
HTML against native endpoint mappings and shipped assets before archiving; Linux container
input uses that same validated portable output. No fingerprint filename is fixed in source.
For local packaging evidence, `tools/PackagePortableRelease.ps1` optionally accepts
`-OutputRoot` (stages/archives) and `-BuildEvidenceRoot` (per-runtime build intermediates/
outputs). Defaults remain `releases` and normal project build paths. Custom roots must
be separate real directories outside source/private state and their ancestors; the
ordinary consistent seed snapshot, real source backup hook and privacy checks still run.

Unexpected production document failures, including sign-in form rendering, return a
standalone non-cacheable HTTP 500 rather than repeatedly redirecting to sign-in. Normal
login redirects remain. See the authentication guide for diagnostics and security boundaries.

## Private server HTTP logs

Logging normally runs in the background with no operator configuration. The single
application-wide logging store is `<DataRoot>/logs/logs.db` (default `Storage/logs/logs.db`), including its
SQLite sidecars. Portable mode uses the executable directory as content root. HTTP
request/exception tables are the initial producers in this shared store; there is
no separate per-protocol database. There is no log viewer or download endpoint.

`HttpRequests` records the framework request ID, original method/path/raw query,
actual UTC entry/terminal timestamps, incoming authentication state (`unobserved`,
`anonymous`, `account`, `bypass`), nullable canonical account GUID, observed status,
completed/incomplete outcome and monotonic elapsed milliseconds. `Id` is only local
row linkage; `RequestId` is not globally unique. `HttpExceptions` links observations
by request row and ordinal with UTC, type, full message and `ExceptionText`: an
allowlisted JSON graph of type/message/available stack and all inner/aggregate
branches. Repeated root objects are deduplicated; different wrappers remain distinct.
Release builds may not have source line numbers. Exception Data/custom properties,
formatted logger messages/scopes, bodies, cookies, credential headers, IP and user
agent are not collected. Publishing's deliberately suppressed native exceptions
are not reconstructed. Full messages and supplied raw queries can nevertheless
contain sensitive information: keep this entire directory private, never attach
real logs to public issues or publish them. No redaction or encryption is promised;
the operator's filesystem permissions protect local readability.

API requests and generated `/exports` downloads are included, including denials,
binding/method/missing-route failures. Account HTML, canonical GET draft-image
previews, routine images/static assets and circuits are excluded. Original raw
targets preserve escapes, duplicate query keys and a leading `?`; absolute-form
scheme/authority are removed. If the server supplies no raw target, encoded framework
PathBase+Path and QueryString are used instead (not a wire-byte-fidelity promise).
Authentication is sampled once before admission: later sign-in/out or revocation
does not change the incoming actor. Bypass has no account GUID. Error documents may
display an Activity ID rather than the persisted framework request ID.

Entry-to-start measures the OnStarting observation, not delivery of headers.
Entry-to-terminal is total time; start-to-completion is only available for a
normally completed response with a start observation. Missing boundaries are NULL.
Abort plus original execution unwind can seal an incomplete row before later
framework errors; the winning terminal freezes exceptions permanently. Incomplete
status is only the last safely observed started response, otherwise NULL. Completed
HTTP500 is still completed. None of these facts proves browser receipt or whether a
business transaction committed. Hangs, crashes and exceptional finalization can
leave no row; late exceptions remain in native diagnostics.

The queue holds **256 envelopes plus at most one writer-owned envelope**, not a hard
byte/memory budget. Full exception detail is not truncated. Requests never wait for
queue capacity or SQLite. Startup/unavailable/full/capture/write/shutdown losses
are possible. One background writer creates a new/empty store atomically, validates
the exact existing schema and writes a request plus all exceptions in one transaction
(foreign keys ON, DELETE journal, FULL synchronous). Incompatible/corrupt stores are
unavailable, never automatically repaired, migrated, cleared or replaced. Native
SQLite journal recovery still applies. Busy timeout is **1 second**, not an OS IO
deadline. Failed/uncertain envelopes are not retried; an uncertain commit may exist.
Unavailable queued/new records are dropped, with reopen attempts every **30 seconds**.
Writer-only stderr summaries at most once per **30 seconds** contain loss reason
counts/readiness/safe SQLite codes, never dropped payloads. Shutdown closes admission,
attempts FIFO drain for at most **5 seconds of host wait**, then abandons remaining
work. Active synchronous IO is not forcibly terminated or cross-thread disposed.

There is **no retention, rotation, pruning, disk limit or automatic log backup**.
Accumulated growth is unbounded and operator-owned; do not rely on these lossy logs
for audit/history or recovery. The existing build backup remains logic-DB-only.
Logs are excluded from source/build/publish/portable artifacts, browser cache,
logic exchange and record history. Inspect privately with a SQLite client's
**read-only** connection, for example:

```sql
SELECT Id, RequestId, Method, Path, QueryString, AuthenticationState, AccountId,
       ObservedStatusCode, Outcome, TotalElapsedMs,
       ResponseStartElapsedMs, ResponseCompletionElapsedMs
FROM HttpRequests ORDER BY Id DESC LIMIT 20;
SELECT e.* FROM HttpExceptions AS e
JOIN HttpRequests AS r ON r.Id=e.HttpRequestId
WHERE r.Id=<selected local request row ID> ORDER BY e.Ordinal;
```

Do not point a writable tool at the live store or copy only its main file during
active writes. No automated log-management/recovery workflow is supplied.

## Create a Room From a Scene Dump

The Scene Dumper mod is not included with Logic Manager. Obtain it separately,
then install it into `Hollow Knight Silksong\BepInEx\plugins\` before continuing.

1. Launch Silksong and load a game.
2. Press `F5` to enable the scene dumper.
3. Walk into a new room.
4. Open the `BepInEx\plugins\scenedump\` folder. The mod creates this folder and saves two JSON files for each room: a scene-load dump and a scene-ready dump.
5. In the tool, select `scene` in the sidebar and import the room’s scene-ready JSON file.
6. Review the detected exits and checks, then import the room.

When the tool cannot match the dump to an existing room, it creates an ungrouped
room. The imported room is a starting point: complete its information and logic
using the sections below and the [Logic Guide](LOGIC.md).

## Complete the Room

There is no general Save button. Text saves when focus leaves the field;
checkboxes, verification choices, ordering, and archive actions save
immediately. The final blank row in each active table is used to create a new
record: enter meaningful information, then leave the field.

Warnings and danger styling identify incomplete, duplicate, ambiguous, or
unresolved information. They do not prevent you from saving data that still
needs research.

### Room Details

- **Room name** is the reader-facing room name.
- **Reference ID** is the room's required key. Use it when linking transitions
  and area-map scenes to this room.
- **Game ID** is the optional raw in-game scene identifier.
- **Contributors** is optional freeform credit text.
- **Comments** are room-wide Markdown documentation. Click the comments area to
  edit its Markdown source.

### Subrooms

Subrooms are optional logical divisions within a room. Add them when different
parts of a room are separated by movement, platforming, or state requirements.

- **Name** is the reader-facing subroom name.
- **Reference ID** is the local key used by transitions, connections, and checks
  in this room.
- **Notes** are optional plain-text documentation.

The scene-rectangle action draws a subroom frame in the scene view. It helps
visualize the room but does not define gameplay logic.

### Room Transitions

The tool calls room exits **room transitions**. Add one row for each loading
zone that moves the player to another room.

- **Alias** is the local loading-zone shorthand. Keep it short and unique within
  the room.
- **Name** is the reader-facing exit name.
- **From subroom** identifies the subroom containing the exit. Fill it in when
  the room has subrooms.
- **Destination room** is the destination room's Reference ID.
- **Destination alias** is the alias of the matching exit in that destination
  room.
- **Requirements** and **Notes** document how to reach and use the exit.

The inverse-state indicator helps identify the matching reverse exit. It can
open that room or offer to set up a missing inverse without overwriting existing
destination text.

### Subroom Connections

Connections document internal room traversal. They are directed: a path from
one subroom to another is not automatically valid in reverse.

- Enter an **alias**, **name**, source subroom, destination subroom,
  **requirements**, and optional **notes**.
- Create the exact reverse row when a route is traversable in both directions.
- Use the same alias for the two directions of one pathway.

### Checks and State

Checks cover more than collectible locations. Use them for checks, bosses,
switches, shops, quests, lore, or other relevant room state.

- Enter a **name**, optional **subroom**, **requirements**, and optional
  **notes**.
- **APW** means the record is included in APWorld. New checks default to being
  included.

### Requirements, TODO, and Verification

- **Requirements** are freeform text. The tool does not parse them or restrict
  their format.
- **TODO** marks a transition, connection, or check as needing more work.
- **Verification** is `Not verified`, `Unknown`, or `Verified`. New transitions,
  connections, and checks start as `Unknown`.

Use the [Logic Guide](LOGIC.md) for the intended standard for requirements,
subrooms, directed connections, and checks.

## Link the Room to the Area Map

Map links require imported map data. If the landing map has no map scenes to
edit, import the shared map manifest before attempting to link rooms.

1. On the landing map, select `edit map links`. You can also use `edit map
   links` from a room's map context or right-click a map shape.
2. Find the map scene by its zone and cache key.
3. Enter the target room's **Reference ID** in the **room reference** field.
4. Select `apply`.
5. Confirm that the link state is resolved.

Only a uniquely resolved, active room becomes a clickable linked map shape.
`merge map scenes into rooms` is a convenience for blank links only; it matches
conservatively and leaves ambiguous or existing links unchanged.

## Capture a Scene Image

An active room's combined bounds/image editor uses minimum and maximum scene
coordinates, pixels-per-unit scales and original-source pixel offsets. Valid bounds
and positive scales are required to Apply. The scene image also requires finite
pixel offsets and built source preview/tiles; opaque black needs neither offsets
nor source assets. Map links are optional.

1. Open the room's scene pane and select `bounds and image`.
2. Review the source image against the room bounds, subrooms, exits, connections,
   and checks.
3. Enter all four bounds and adjust **pixels/unit X/Y**, **pixel offset X/Y**, and
   **Use scene image**. Cyan minimum edges can be dragged; maxima are numeric-only.
   `reset recommendation` uses scales 6.35 and recommends pixel offsets from optional
   calibrated map context or the fixed source center. Ctrl+drag positions the image;
   numeric scale edits and Ctrl+wheel leave pixel offsets fixed. Editing bounds
   never recenters the image. Offsets are in original 20500 x 15000 source pixels,
   with X rightward and Y upward from the source bottom-left, not preview pixels.
4. Select `apply` to generate the image behind the room's scene annotations.

Apply generates and saves bounds, mapping and mode together; Cancel leaves them
unchanged. `clear bounds` resets minima to zero/maxima to blank, retains mapping,
mode and file, and hides an existing image as needing recapture. Scene-dump maximum
changes also stale existing images. Annotation coordinates never change. This
uses one configured source, not arbitrary image uploads. Both scene-image and black
round each effective size times its scale to the nearest integer, midpoint-to-even,
with a one-pixel minimum and no 4096 cap.

## Create a Room Manually

Use manual creation only when a scene dump is unavailable.

1. Create or select a room group in the sidebar.
2. Select `page` in that group to create a room.
3. Enter the room details, then add its transitions, subrooms, directed
   connections, checks, and requirements as described above.
4. Follow the [Logic Guide](LOGIC.md) to complete the room's logic.
