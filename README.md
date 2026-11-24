# Silksong Rando Logic Manager

Use Logic Manager to browse and edit Silksong randomizer logic. To run your own
copy, follow the [Docker setup guide](readme/CONTAINERS.md).

For a new installation, create two private folders on your machine: one for
`appsettings.json` and one for `silksong-rando-logic.db` and the files Logic Manager
creates. The guide shows how to tell Docker where those folders are. Get the
starting database from the [public logic database page](https://github.com/zerounit-dev/silksong-rando-ap-logic/blob/main/silksong-rando-logic.db).
If you already run Logic Manager,
back up and reuse your existing folders. **Do not replace your database.**

Authentication setup, private state, upgrade and recovery instructions:
[Authentication guide](readme/AUTHENTICATION.md).
Authentication uses native Identity cookies: 30-minute HTTP/
circuit stamp validation, persistent 400-day sliding tickets, and current-browser
cookie logout (not copied-cookie/other-circuit revocation). The cutover requires one
fresh login and an accounts-only forward migration; private account/key backups and
operator-controlled maintenance are required.

Access is controlled by four independent account permissions:
Manage admins, Manage users, Edit logic, Publish documentation. Admin (all four),
Contributor (Edit logic only), Read only (none) and derived Custom are checkbox presets,
not authority. Accounts entry needs either manager; commands also check current target
and requested protection grants. Publishing needs Publish documentation, not management
or editing. The forward migration backfills the old-role mappings,
rotates stamps and drops Role after RemoveSessions. One fresh login is expected.
Custom cannot be losslessly downgraded; rollback requires
matching consistent private account/key state and binaries, not merely an old image.

## Operator configuration and state

The actual project settings are `Config/appsettings.json` and native environment-specific
`Config/appsettings.{Environment}.json` (including relocated Development logging defaults),
followed by unprefixed environment and CLI overrides. Explicit `Authentication:Mode` is required;
there is no automatic bypass. Mutable state defaults to `Storage/`, configurable via
`Storage:DataRoot`; `Data/` retains C# definitions/migrations only (plus the untouched
offline test fixture). Runtime databases, backups, keys, images, and history belong
under the selected Storage root; preserve existing state when relocating an installation.
Select container `/config` through `LogicManager__ConfigDirectory` and `/data` through
`Storage__DataRoot`. See the authentication guide for deliberate transition, native
hosting/array precedence, recovery, permissions, consistent backups and exact
`LogicManagerPrivateRoots` / scripts `PrivateRoots` build/publication usage.
Published source includes nonsecret Config defaults, an empty Storage marker, and
approved scene assets, but not the logic database. Private accounts, keys, backups, history, logs, SQLite sidecars,
staging, source master and relocation rollback remain ignored. Normal builds/publishes
copy the two settings files, not runtime Storage contents.
Keep credentials out of tracked settings: use environment/CLI or an external directory
selected by `LogicManager__ConfigDirectory`. Source publication independently generates
sanitized required-mode defaults/scaffolding; portable packages generate sanitized bypass
settings, never host credentials or mode. Custom in-source private roots require matching
operator declarations and ignore rules. Tooling does not discover arbitrary secrets.
No runtime outside-source restriction or automatic file move is introduced.

## Native documentation publishing

Open `/tools/publishing` directly or through the Tools header (Interactive Server). This
publishes **documentation only**; it never builds/releases the tool or invokes the
independent Python/source-publication pipelines. The feature is disabled by default.
The independent Publish documentation permission protects the entire workflow, status,
previews and detached command admission; neither management permission nor Edit logic
is required or sufficient. Enabling publishing is not authentication. Do not
expose an unprotected installation to untrusted users.

Configure `DocumentationPublishing` in the unified private `Config/appsettings.json`
(or explicit environment/CLI). The retired `data/Publishing/settings.private.json`
provider is no longer read:

```json
{
  "DocumentationPublishing": {
    "Enabled": false,
    "RepositoryUrl": "https://github.com/YOUR-OWNER/YOUR-LOGIC-REPOSITORY.git",
    "Branch": "YOUR-EXISTING-BRANCH",
    "AuthorName": "YOUR-COMMIT-AUTHOR",
    "AuthorEmail": "YOUR-COMMIT-EMAIL",
    "GitHubUsername": "YOUR-GITHUB-USERNAME",
    "GitHubToken": "YOUR-PRIVATE-TOKEN"
  }
}
```

The selected Config base/environment files use native precedence; environment variables and command
line override them. Keep secrets outside tracked Config files, e.g.
`DocumentationPublishing__GitHubToken`. Set Enabled to true deliberately. Restart
under operator control after configuration changes; the service captures its setup
once. Never put real credentials in public appsettings, source, URLs or commit messages.
Create a fine-grained GitHub PAT scoped only to the selected repository with
**Contents: read and write** (and complete any organization approval). The token is
used by native credential callbacks, not saved in Git configuration. Configuration
readiness is not proof that GitHub accepted the credentials. GitHub Apps are not
implemented. No Git executable, Python or installed .NET SDK is needed at runtime;
LibGit2Sharp/native dependencies and the licensed fixed Noto Sans font ship with the app.

The GitHub HTTPS repository and branch must already exist (including an initial
commit). Prepare fetches/clones only on explicit action; it can fast-forward a clean
checkout but never creates an absent remote branch or merges divergent work.
Prepare freezes one SQLite online backup and eligible current captures under image
lifecycle admission, then releases admission before exports/rendering. Later edits
do not change the prepared candidate. Review actual changes/removals, expand text
diffs and image previews, supply the exact commit message, then **Commit and push**.
No-change candidates create no commit. A failed/uncertain push retains the exact
commit for **Retry push**, which first reconciles remote history and never recommits.
Navigation/disconnection does not cancel admitted work; reopening reads current status.

`<DataRoot>/Publishing/Repository` and `<DataRoot>/Publishing/Staging` are private operational
material, not static assets or a run-history browser. Keep the whole subtree private,
retain it across application upgrades, and never include it in public source,
build output or portable packages. One process owns its exclusive writer lease;
shared multi-replica writers, linked worktrees, symlinks/submodules and Git content
filters are unsupported. The application does not recapture images or repair logic.
Unused inherited Git LFS/filter command definitions at system/user/XDG/Windows
ProgramData levels do not block native publishing; no global cleanup or LFS uninstall
is needed. Repository/application-level filter definitions, unsupported committed
filter/encoding attributes, and forbidden storage/hooks/attribute-file/include/URL
settings still block with a safe category. This is a narrow inactive-definition
exception, not isolation from all host Git attributes, CRLF or ident behavior.
Boss/gauntlet Combat Requirements and Intended pogo-off violations block generation.
Native PNGs may differ from historical Python output; subsequent unchanged native
renders must have identical bytes, including after restart. Font/native-renderer
upgrades require deliberate renderer-change awareness; raw geometry WebPs stay exact.

### Publishing recovery

Interrupted preparation can be discarded/reprepared. An early preparation failure
caused only by inherited LFS definitions needs no config repair:
after the operator updates/restarts the app, use the existing **Discard candidate**
action, then **Prepare** and review again. Do not manually remove the workspace.
On Windows, admitted staging cleanup removes the ReadOnly file bit from native Git
pack/index files before deleting disposable staging. It does not change the adopted
checkout, bypass locks/permissions or make unresolved evidence discardable. This
cleanup correction does not diagnose or fix the separately investigated preparation
exception; staging without a journal is not newly admitted for discard.
Windows native publishing also enables `core.longpaths` only in its own repository
configuration, before checkout/status, so existing long annotation filenames need
not be renamed. It does not change user/system Git settings or move storage roots.
Unexpected failures use the existing **Application error** modal with full original
exception details and **Copy details** on the initiating mounted page. Closing it
does not reopen it on each refresh. Other pages/tabs (even the same account) cannot
receive those details; ordinary reconnect of the same mounted page preserves delivery,
but navigation/new circuit/process restart does not recover them. Expected setup/
validation blocks remain inline. No console hunting is required to obtain an available
unexpected failure. This fixes diagnostic delivery, not proof of the lost exception's cause.
This tool initializes only after its Interactive Server circuit mounts (no prerender),
so initial failures use that same page's modal rather than a discarded prerender scope.
A verified precommit interruption restores only operation-owned files/index; an already
advanced exact commit is retained for push reconciliation. **Discard never deletes unresolved
rollback or commit evidence.** On divergence, corruption, unexpected checkout/index
changes, path-safety errors or uncertain evidence, stop publishing attempts. Preserve
the complete private workspace (including journal/preimages) and displayed commit ID.
Have an operator inspect local/remote history and deliberately reconcile the checkout,
then revalidate or prepare/review anew. Do not blindly delete staging or edit the
journal. No automatic reset, clean, merge, rebase, force push or destructive fallback
exists. If a push response was lost, retry the retained commit before attempting a
new publication. The ordinary logic backup-on-build hook remains unchanged and does
not back up private publishing credentials/evidence.

Logic Manager helps contributors document rooms, exits, connections, checks,
requirements, and map details for the Silksong Archipelago randomizer (Batsong).

Massive thank you to **RainingChain** and **IdoManti** for allowing us to generate
room annotations using the 25%-resolution image of their amazing high-detail
Silksong map.

[You can find the interactive version of their map and their incredible work here.](https://scripterswar.com/silksong/map)

## Download

Use the versioned Linux AMD64 Docker image for new installations. Older releases
still have their original Windows, Linux, and macOS downloads. See the
[Docker setup guide](readme/CONTAINERS.md) for the Compose example,
database download and folder setup.

## Build and run from source

### Application URLs

The current V3 client opens at `/`; the retained server application is at `/v2/`
(rooms: `/v2/rooms/{id}`, catalogue: `/v2/requirements`, scene: `/v2/scene-import`).
The authoritative server tools remain `/tools/room-import` and `/tools/map-manifest`.
API URLs including `/api/v3/`, shared media and downloads are unchanged. The former
`/v3/` UI URLs have no aliases or redirects. This relocation does not retire the
server application or declare feature parity. See documentation/ROUTE_CUTOVER_CONTRACT.md.

Source builds require the .NET 10 SDK. Before running a fresh source checkout,
download `silksong-rando-logic.db` from the public database page above and put it in your
selected Storage folder. If you already use Logic Manager, use your existing
data folder instead. After preparing your data and settings, run:

```powershell
dotnet restore "Silksong Rando Logic Manager.slnx"
dotnet build "Silksong Rando Logic Manager.slnx" -c Release
dotnet run --project "Silksong Rando Logic Manager/Silksong Rando Logic Manager/Silksong Rando Logic Manager.csproj"
```

The application runs as a local server-hosted Blazor app. Follow the URL shown
in the terminal and keep that process running while using the application.

### Release metadata preparation

Before packaging/tagging an approved release, update the shared application `Version`
in `project/Directory.Build.props` to that approved version, or pass an explicit
MSBuild property (for example `dotnet publish <application.csproj> -c Release -p:Version=1.2.3`).
All five application projects inherit this baseline in ordinary builds and publishes;
the current `0.13.0` matches the existing published release, not a new release.
The loading screen reads compiled informational release metadata and the existing UTC
build stamp. The five app projects disable SDK commit appending to informational
version, preserving the exact authored release/prerelease/build metadata; release
metadata does not replace the internal `app-` build identity.

The internal identity and executable graph are generated only in an admitted application
Build, before compilation and native static-asset discovery. Design-time work, reference/
asset getters and publishing existing outputs must reuse completed outputs, not mint
another identity. Shared compiler/IDE dependencies preserve coherent incremental builds
without requiring Clean/Rebuild or disabling the IDE up-to-date check.

## Data and upgrades

In a fresh source checkout, place the verified database here unless you set a
different `Storage:DataRoot`:

```text
Silksong Rando Logic Manager/Silksong Rando Logic Manager/Storage/silksong-rando-logic.db
```

The running application uses the selected DataRoot (default `Storage/`). The
published source includes approved scene assets, but not the logic database,
accounts, keys, backups, history, logs or recovery files.
Existing external installs deliberately relocate/consolidate once; there is no dual
reader or automatic application move. Never copy Data C# as runtime state. See the guide.

Back up configuration and consistent state before upgrades or major imports. Retain
both Config/Storage (or configured roots) while replacing application/runtime files.
Do not overwrite retained values/state with first-install config or seed. The existing
build hook backs up **source-project** `Storage/silksong-rando-logic.db` into
`Storage/backups`; it does not follow external DataRoot settings or protect accounts/keys/images.

## Documentation

- [Application guide](readme/README.md)
- [Logic guide](readme/LOGIC.md)

## License

Except for separately attributed map imagery, this project is licensed under the
MIT License.
