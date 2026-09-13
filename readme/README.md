# Silksong Rando Logic Manager

Silksong Rando Logic Manager is a local tool for documenting Silksong rooms and
the logic used to traverse them. It organizes rooms, exits, subrooms,
connections, checks, requirements, notes, and verification status. The normal
way to begin a room is by importing information collected by the Scene Dumper.

## Start the App

1. Extract the release archive into a folder. On Windows, extract the
   `win-x64` ZIP. On mainstream glibc Linux, extract the `linux-x64` tarball;
   on an Apple Silicon Mac (including M1), extract the `osx-arm64` tarball:
   `tar -xzf silksong-rando-logic-manager-<platform>.tar.gz`.
2. Run `Launch Silksong Rando Logic Manager.cmd` on Windows, or from a terminal
   run `./Launch\ Silksong\ Rando\ Logic\ Manager.sh` on Linux or macOS.
3. Wait for the tool to open `http://localhost:5000` in your default browser.
4. Keep the application running while you use the tool. Closing the browser tab
   does not close the application.

The tool stores your work in the `data` folder inside `Silksong Rando Logic
Manager`. Before upgrading the tool or performing a major import, close the
application and make a copy of the entire `data` folder. When upgrading, keep
   your existing `data` folder and replace only the application files. Linux
   releases target mainstream glibc distributions; Alpine Linux is not supported.
   The Mac release targets Apple Silicon only and is unsigned, so macOS may
   require you to approve it in Privacy & Security after the first launch attempt.

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

Scene-image capture requires an active room with valid scene dimensions, one
resolved map link with usable bounds, and the configured area-map source image.

1. Open the room's scene pane and select `capture scene image`. After the first
   capture, the action is `recapture scene image`.
2. Review the source image against the room bounds, subrooms, exits, connections,
   and checks.
3. Adjust **scale X**, **scale Y**, **pan X**, and **pan Y**, or use `reset
   estimate` to recalculate the map-derived starting position.
4. Select `apply` to generate the image behind the room's scene annotations.

Changing the room's scene dimensions marks an existing image as needing
recapture and hides it until it is captured again. This feature uses the
configured area-map source image; it is not an arbitrary image-upload tool.

## Create a Room Manually

Use manual creation only when a scene dump is unavailable.

1. Create or select a room group in the sidebar.
2. Select `page` in that group to create a room.
3. Enter the room details, then add its transitions, subrooms, directed
   connections, checks, and requirements as described above.
4. Follow the [Logic Guide](LOGIC.md) to complete the room's logic.
