# Authentication and private operator state

Before startup, explicitly set `Authentication:Mode` to exactly `required` or
`bypass` in the unified operator file `Config/appsettings.json`, or through an
explicit environment/command-line key. `appsettings.example.json` beside this guide
is a nonsecret example, not a runtime file. The project now contains actual tracked,
vetted nonsecret settings under Config, not root appsettings files or a separate auth
mode file. The local source restructure preserves its prior explicit mode exactly;
it is not a new choice of mode. Missing/invalid mode stops startup before
application startup writes; there is no automatic bypass or legacy-file fallback.

## Configuration, paths and precedence

Local defaults, relative to the existing application content root:

- `Data/`: source C# definitions/migrations; an untouched offline test DB fixture is
  not active application state. Do not move or exclude the whole source tree.
- `Config/appsettings.json`: the writable base settings file, including existing
  Authentication, HttpHosting, AllowedHosts, Logging and DocumentationPublishing keys.
  `Config/appsettings.{Environment}.json` follows native environment selection; the
  relocated Development file supplies Development logging only, not production settings.
  Only the two vetted nonsecret source defaults are tracked. Never put host credentials
  into these tracked files; select an external private Config directory or use env/CLI.
- `Storage/`: mutable primary `silksong-rando-logic.db`, `accounts.db`,
  `authentication/keys/`, `history/logic-history.db`, `logs/logs.db`, `scenes/`,
  `scene-source/`, `scene-originals/`, `scene-image-staging/` and all `Publishing/`.

Native precedence, low to high: host fallback, selected Config base JSON, selected
Config environment JSON, Development user secrets (when configured), **unprefixed**
environment, command line. An external locator selects that directory's base/environment
pair, not a second competing local settings source. Native JSON syntax
and existing option validation apply; there is no general unknown-key validator.
Examples: `Authentication__Mode=required`, `Storage__DataRoot=/data`,
`DocumentationPublishing__GitHubToken` (keep its real value private), and
`--Authentication:Mode=required --Storage:DataRoot=/data`.

The bootstrap locator is `LogicManager__ConfigDirectory` or
`--LogicManager:ConfigDirectory=...` (CLI wins), default `Config`. The file cannot
locate itself. `Storage:DataRoot` selects state; relative locator/data paths resolve
against content root, with native Windows/macOS/Linux absolute paths also supported.
Neither setting changes content root, assets/font paths or OS temporary storage.
Portable content root remains the executable directory; its fixed localhost:5000
listener/browser launch is unchanged, **not** made configurable by these settings.

`ASPNETCORE_`/`DOTNET_` host settings select bootstrap environment/roots earlier.
Application-file `Urls` overrides host-fallback `ASPNETCORE_URLS`; same-key unprefixed
`Urls` and CLI override the file. Native Kestrel endpoints outrank the Urls family.
Native indexed arrays merge providers: a shorter environment TrustedProxies list does
not erase trailing file entries. Inspect the complete effective list when changing it.
Resolved paths, authentication and publishing settings are captured once before startup
writes; publishing workspace construction remains lazy. Restart under operator control
to change these.
Native logging/hosting retain their existing supported options lifecycles; this move
adds no blanket hot-reload or restart-only rule.

### Deliberate transition and updates

The source project restructure merges its old root settings and auth mode, relocates
Development defaults, retires those old source files and tracks an empty Storage marker.
The authorized one-time workspace restructure also selectively moved runtime DBs with
all sidecars, backups, keys, history and scene stores into Storage without initialization.
The preexisting destination state is preserved together in private ignored
`Storage/_relocation-rollback/`, with an exact private manifest; never distribute it.
No application startup migration/move/merge was added. Keep that rollback material private
until its owner deliberately decides its disposition; no automatic retention/deletion exists.
For a separate older deployed installation, in an operator-controlled copy consolidate the old auth `mode` as
`Authentication:Mode`; retain its exact required/bypass choice. Consolidate previous
image/native settings and `data/Publishing/settings.private.json` under the existing
DocumentationPublishing section, including the private credential. Retain effective
environment/CLI overrides. The retired auth/private-publishing files are no longer read.
Place existing state under the chosen DataRoot, preserving child names/casing, accounts,
keys and the **entire** publishing recovery workspace. Do not start against a different
empty root expecting legacy data discovery. Ordinary first-install initialization and
store-specific failure behavior are unchanged; there is no primary-existence gate.
The source seed and selected public scene assets now already reside in Storage.
Never copy Data C# as runtime state. Custom/external roots still require deliberate
operator-controlled relocation of complete consistent state before application startup.

Updates replace binaries/image defaults, not retained Config/Storage or custom roots.
Never overwrite retained settings with the generated first-install bypass configuration
or overwrite existing state with a release seed. Config remains writable; future safe
settings writes/migrations are not prohibited, but no migration framework is supplied.

### Containers and filesystem permissions

Explicitly select `LogicManager__ConfigDirectory=/config` and
`Storage__DataRoot=/data`; merely mounting a directory does not select it. Mount the
operator's actual `applications/<app-name>/config` dataset at `/config` read/write and
`applications/<app-name>/mount` at `/data`. Choose real TrueNAS pool paths, process
UID/GID and ACLs for that deployment; no universal values or port are assumed.
Keep configuration private/writable and data writable for SQLite sidecars, keys, images
and publishing recovery. Keep both outside public serving roots; do not add static-file
providers for them. Do not recursively chown or change a live installation as part of
following this guide. For the additional Linux AMD64 image packaging, safe empty-mount
seed initialization and Compose workflow, see [Containers](https://github.com/zerounit-dev/silksong-rando-logic-manager/blob/main/readme/CONTAINERS.md). Existing
application initialization and this guide's auth/hosting/security contract stay unchanged.

### Build and publication private-root declarations

Build/publication tooling does not read the running process's independent configuration.
Default server Config/Storage contents are automatically excluded except the exact
vetted source `Config/appsettings.json` and `Config/appsettings.Development.json` copied
by normal application builds/publishes. Git tracks these nonsecret Config defaults,
`Storage/.gitkeep`, and the deliberate public primary seed plus scene, scene-original,
scene-source preview/build, tile and attribution assets. Private accounts, keys, backups,
history, logs, SQLite sidecars, staging, source master and relocation rollback remain
ignored. This is not permission to store credentials in
the tracked defaults. Portable publication suppresses even these source settings.
Declare every nonstandard
private directory **inside the source tree**, and add corresponding local Git ignore
rules. Undeclared custom in-source roots are operator misconfiguration, not automatically
discoverable secrets. External roots outside the source tree cannot enter recursive
source copying. These are tooling inputs, not runtime path restrictions.

From `project/`, for one example custom private directory:

```powershell
dotnet build "Silksong Rando Logic Manager/Silksong Rando Logic Manager/Silksong Rando Logic Manager.csproj" -c Release '-p:LogicManagerPrivateRoots=LocalPrivate'
./tools/PackagePortableRelease.ps1 -RuntimeIdentifier win-x64 -PrivateRoots @('LocalPrivate')
```

From the umbrella workspace (publication-owner action, not an acceptance-build step):

```powershell
./publish-logic-manager-release.ps1 -PrivateRoots @('LocalPrivate')
```

Relative declarations resolve against `project/` (publisher SourceRoot), consistently
across referenced projects; absolute literal directories are supported. MSBuild takes
a semicolon list; on its command line encode separators as `%3B`, for example
`'-p:LogicManagerPrivateRoots=LocalPrivate%3BOtherPrivate'`. Scripts take a string array
and the portable builder forwards declarations to publish. Empty/glob declarations
are invalid; declarations that remove required application code/assets fail tooling.
Source/portable declarations overlapping the deliberately public Storage seed/assets also fail:
choose an uncontaminated public seed before distribution rather than silently excluding
required seed assets or copying declared private material during reconstruction.
No auto-discovery, marker, configuration rewrite or migration is involved.
Source publication never copies host Config/Storage: it generates nonsecret required-mode
base settings, Development logging defaults and an inert Storage marker, validating exact
contents before the existing privacy scanner. It reconstructs only the deliberate Storage
primary seed through consistent snapshotting and public scene/source/original assets;
portable staging inserts that selected seed/assets into Storage and generates only
sanitized first-install Config/appsettings.json, never host settings/accounts/keys.

Portable packages deliberately generate bypass mode for local standalone use.
**Never expose bypass through a tunnel.** Requests arriving through a tunnel can
look local. Before sharing, change to required mode and restart under operator
control. Required-mode cookies require HTTPS; arrange HTTPS termination at the
tunnel without trusting arbitrary forwarded headers as identity.

## HTTP hosting: standalone and host-preserving cloudflared

Authentication mode and hosting use the same unified configuration graph above.
Neither forwarding nor a listener address selects authentication mode.

Standalone `appsettings.json` example (or omit HttpHosting for these defaults):

```json
{
  "HttpHosting": { "ForwardingEnabled": false, "TrustedProxies": [] },
  "AllowedHosts": "localhost;127.0.0.1;[::1]"
}
```

Local HTTP remains supported, including portable operation. The application does not
redirect every nonportable request to HTTPS. Non-Development native HSTS still applies
to recognized HTTPS, with the framework's localhost exclusions.

Illustrative **required-mode**, host-preserving cloudflared configuration:

```json
{
  "HttpHosting": {
    "ForwardingEnabled": true,
    "TrustedProxies": [ "127.0.0.1" ]
  },
  "AllowedHosts": "logic.example.org;localhost;127.0.0.1"
}
```

Replace examples with the deployment's browser-facing names and **immediate connector
peer**. Loopback is correct only if it is the actual peer; a container connector may
have another address. Trust only specific unicast/loopback IP literals. No subnet,
hostname, wildcard, unspecified or multicast address is accepted. Enabled forwarding
requires a nonempty list and explicit non-wildcard native AllowedHosts. Include intended
local names too. Host filtering is not authentication.

Cloudflare must enforce public HTTPS and control visitor-protocol forwarding.
cloudflared must preserve browser-facing Host; do **not** set an internal
`httpHostHeader` rewrite for this bounded configuration. Either HTTP or HTTPS origin
connections are supported; the app neither infers/stores that choice nor manages origin
TLS. Configure/verify the chosen origin/TLS separately. Only native X-Forwarded-Proto
from configured immediate peers is processed, one hop. X-Forwarded-For/Host/Prefix and
CF identity headers grant no identity or permission. The app does not assume cloudflared
sanitizes X-Forwarded-Host.

Startup rejects effective `ForwardedHeaders_Enabled=true` (case-insensitive), including
normal `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` input, before application startup
writes. Unset/disable the conflicting setting and use HttpHosting; it is not silently
overridden. The guard reads effective configuration, not just an environment variable.

Isolate the origin listener from untrusted direct access. Trusting a loopback connector
also trusts local processes able to connect from that address; it does not authenticate
cloudflared. Secure host/container networking and private configuration. Additional
proxy chains or Host rewriting need a separately settled contract. Required-mode cookies
remain Secure Always; forwarding never enables bypass. Verify public HTTPS, recognized
scheme/Origin checks (including disconnect), cookies, host filtering and untrusted-header
rejection in the actual deployment. Source/build do not establish deployment security.

## Bootstrap and recovery

In deliberate bypass, `/account/admin` works without an account. Create an enabled
account using the Admin preset (all four permissions) there and manually copy the generated temporary password from its one-time
response. Stop normally, select required mode and restart. Sign in, replace the
temporary password, then sign in again with the new password. No fixed account or
password is supplied. Required mode with no usable accounts stays locked.

Accounts have four independent permissions: **Manage admins**, **Manage users**,
**Edit logic**, **Publish documentation**. Admin selects all four, Contributor selects
only Edit logic, Read only selects none; every other set is Custom. Presets are only
checkbox conveniences, never roles. Custom preserves its exact boxes on reopen; selecting
Custom does not reset them. An enabled account with no permissions can sign in, view and
download normally, but cannot perform protected writes. Temporary-password replacement
restrictions override all permissions.

Either management permission opens Accounts and lists all account metadata. Manage admins
manages currently protected accounts (Manage admins checked, even when disabled) and
creates protected accounts. Manage users manages/creates ordinary accounts and can delegate
all ordinary permissions, including ones it lacks. Neither implies editing or publishing.
Promoting an existing ordinary account requires **both** management permissions. Removing
protection requires Manage admins; a submitted demotion cannot evade the current-target check.
Unavailable row actions are disabled. Ordinary creation defaults to Read only/enabled;
Manage-admins-only creation defaults to protected Admin. The server enforces the boxes,
not the preset or disabled controls.

Authorized managers can rename, enable/disable, change permissions, generate a replacement password,
or permanently delete their permitted targets. Reset and deletion require the current username
as confirmation. Losing the temporary-password response requires another reset;
there is no secret retrieval, registration, email recovery or ordinary password
change page. All created/reset passwords require replacement before normal use.

Actual rename, permission/enabled changes and reset rotate the account security/concurrency stamps;
deletion removes the account. Native HTTP security-stamp validation and connected
server-tool circuit revalidation run at 30-minute intervals. Detection waits for a
due request/check (not a hard real-time deadline); actual new operations can proceed
under previously validated permissions in that window. Already-admitted work may finish.
Self-change clears the current browser cookie; unchanged saves do not sign you out.
Removal of protection from one's own account and removal of the last enabled account
with Manage admins are allowed: recover through explicit bypass.
Deliberate bypass startup rotates existing account stamps, retaining accounts and
keys. Bypass remains account-free with zero accounts and does not authenticate account
cookies. Return to required mode only after completing operator recovery.

## Mounted workspace and departure

Accounts own work; native protected Identity application cookies carry credentials.
Valid reauthentication for the same account can continue using its local drafts and transient
room/image/import handles. The account-derived workspace key is a non-credential
consistency precondition, not a substitute for authenticated-principal, permission, ownership or
CSRF checks. An old account's document cannot adopt another account's access.

The server rejects protected operations when credentials are invalid or current
permissions forbid them. In a mounted workspace, failed actions may show ordinary
local errors; authentication failure does not trigger a special freeze, forced login
navigation or automatic draft discard. Reload is an explicit recovery option, with
normal server login and password-replacement admission as required. **Reloading or
navigating away may lose local drafts and workflow state.** Work already admitted
may have committed; inspect current data and outcome information before acting
again. Failed or uncertain work is not automatically replayed.

Client logout retains exact queue-aware confirmation/wait/cancel admission, then
submits an ordinary antiforgery-protected native form. It clears this browser's cookie
and replaces the initiating document. It does not revoke a saved copy of the cookie,
other devices, or already-connected circuits with an unchanged account stamp. Such
circuits can remain authenticated beyond 30 minutes; circuit validation does not enforce
browser-cookie expiry. A copied cookie can remain valid/renewable until invalidation
or expiry. Logout does not rotate the account stamp or broadcast to other tabs.
The account precondition prevents an old account
A document from logging out account B. There is no mounted acknowledgement,
execution seal or retry; the browser owns response/navigation failures, and local
input preservation after departure is not guaranteed.

## Persistence, upgrade and private backups

Preserve `Config/appsettings.json`, `<DataRoot>/accounts.db`, and
`<DataRoot>/authentication/keys/` during upgrades. Never publish these files, SQLite
sidecars or account backup variants. The existing build backup covers only the
source-project `Storage/silksong-rando-logic.db` into `Storage/backups`, using the same
SQLite-consistent helper/hook after the relocation. It does not follow selected external deployment
DataRoot, accounts, keys, images or publishing evidence.

Keep authentication state accessible only to the operator. Persisted keys are not
automatically encrypted at rest; filesystem permissions, disk security and private
backups matter. TLS and password hashing do not protect an attacker who can read
both account storage and the key directory. Retain old keys for existing cookies.
Missing/corrupted keys can sign everyone out; they do not recover passwords.
Use deliberate bypass to generate fresh credentials when needed. Corruption never
authorizes automatic reseeding or a silent downgrade.

Private backups are operator-owned: use a SQLite-supported consistent online
backup, or copy while stopped with explicit operator approval. Never copy only a
live main database file while WAL transactions may exist. Back up retained keys and
unified configuration privately too. Include the consistent primary/accounts DBs,
images and complete publishing recovery workspace; history/logs may be backed up if
desired. This is operator guidance, not a new schedule/retention/backup feature.
Restoring old account storage may restore old
passwords and stamps: deliberately start bypass to rotate account stamps before returning
to required service. Do not distribute private recovery material with source or
portable artifacts.

Native persistent tickets expire after 400 days without renewal, with sliding renewal
on eligible HTTP responses. Successful native stamp validation also rebuilds claims and
can renew the cookie. Continued use can extend sign-in without a fixed total age, but
circuit messages alone cannot issue renewal cookies. Browser caps, eviction, private
mode, clearing cookies and lost keys can require login earlier. There is no Remember me
step, keepalive endpoint, server ticket/session registry or promise of endless idle sign-in.

Sign-in requires cookies to be enabled and retained. Malformed/revoked cookies follow
native rejection and normal login, without the former ticket-store extra credential-entry
step. Ordinary stale forms may fail antiforgery verification; reopen the form. Passwords
are never replayed automatically.

### Native-cookie cutover

Source engineering does not deploy, restart or migrate a live installation. The operator
owns the later maintenance action and matching-artifact sign-in checks. Before upgrading,
make the consistent private account/key/config backup described above; the logic-only
build backup does not protect accounts. Retain matching pre-cutover binaries if rollback
is needed.

The forward accounts migration drops only the old `Sessions` table (including its index
and foreign key); account IDs, hashes, stamps, Identity support tables and keys remain.
New databases apply the historical account migration followed by this removal. Old custom
ticket cookies cannot unprotect under the native Identity application scheme: one fresh
login is expected at cutover, and old open forms may need reopening. Later compatible
native upgrades/restarts with retained state and keys preserve sign-in.

An image/binary-only rollback is not a data rollback: old binaries require the removed
table. Restore matching consistent private pre-cutover state/binaries under operator
control; no automatic reverse migration or reconstruction of old logins is supplied.
Restoring both old account stamps/passwords and keys unchanged can restore credential
validity; do not assume cryptographic revocation survives that restoration. Explicit
bypass recovery rotates stamps, which are detected at native validation boundaries.

### Permissions cutover (AUTH-PERM1)

The source candidate adds `20261114010000_AccountPermissions` after RemoveSessions.
It adds four Boolean account fields, backfills existing Admin to all four, Contributor
to Edit logic only and ReadOnly to none, changes security/concurrency stamps, then removes
Role. Unsupported historical role values fail the migration transaction with an actionable
error; no unknown-role fallback grants or password resets occur. IDs, names, enabled/
replacement/lockout state, hashes, Identity support data, keys and private locations remain.
New databases reach the same target through the complete migration chain.

Operator maintenance/consistent private account+key backup is required before applying
this migration; it was **not** run during source delivery. The logic-only build backup
does not protect accounts. Old role cookies cannot authorize permissions and rotated
stamps require one fresh login at native detection. Stale open documents/forms may need
reload/reopen. HTTP/circuit timing, 400-day sliding cookies and logout limits above remain.
Detached publishing checks the originally presented stamp against the current enabled,
non-replacement account's Publish documentation permission before admitting a new command;
already-admitted work may finish.

Custom permissions have no lossless old-role mapping. Automatic Down is deliberately
unsupported; never collapse Custom to a role. Old binaries expect Role, so image-only
rollback is not state rollback. Restore matching binaries and consistent private
pre-cutover account/key/config state under operator control. Restored stamps/keys can
restore old credential validity; the explicit bypass recovery procedure above still applies.
