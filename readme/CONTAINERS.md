# Linux AMD64 container release and deployment

Docker is the self-hosting option for new Logic Manager releases. Previous Windows,
Linux and macOS standalone downloads remain available from their original releases.
There is no Scene Dumper or generated-logic publication in this workflow.

## Set up a new installation

You need Docker with Linux AMD64 support and a versioned Logic Manager image from
the [application releases](https://github.com/zerounit-dev/silksong-rando-logic-manager/releases).
Already running Logic Manager? Skip this section, back up your installation, and
reuse your existing configuration and data folders.

1. Create two private folders on your machine. In this example they are
   `/srv/logic-manager/config` and `/srv/logic-manager/data`. Use actual absolute
   paths on your machine, not the example paths if those do not suit your setup.
   Keep the new folders writable by you while you put the files in place. If you
   can't create folders under `/srv`, choose a private location you can write to.
   You'll assign Docker's user (`1654:1654`) access in step 5, before starting the app.
2. Copy [the example settings](appsettings.example.json) to the config folder as
   `appsettings.json`. Keep that file private and follow the [authentication
   guide](AUTHENTICATION.md#bootstrap-and-recovery) to create your first admin.
3. Open the [public logic database page](https://github.com/zerounit-dev/silksong-rando-ap-logic/blob/main/silksong-rando-logic.db)
   and select **Download raw file**. Put `silksong-rando-logic.db` in the new
   data folder. The app also
   keeps private accounts, keys, captures and backups in that folder.
4. Save the [Docker Compose example](https://github.com/zerounit-dev/silksong-rando-logic-manager/blob/main/tools/container/compose.example.yaml)
   as `compose.yaml`. In the same
   directory, create a file named `.env` and enter your image and host paths:

   ```dotenv
   LOGIC_MANAGER_IMAGE=ghcr.io/zerounit-dev/silksong-rando-logic-manager:YOUR-RELEASE-VERSION
   LOGIC_MANAGER_CONFIG=/srv/logic-manager/config
   LOGIC_MANAGER_DATA=/srv/logic-manager/data
   ```

   Replace the version placeholder with the tag (or use the full image digest
   reference from the release notes). Docker makes your config folder available
   as `/config` and your data folder as `/data`; leave those container paths alone.
5. Copy the included scene files into the **new** data folder. From the folder
   with `compose.yaml`, run `docker compose create` (this does not start the app),
   then run `docker compose cp logic-manager:/opt/logic-manager-seed/. /srv/logic-manager/data/`,
   substituting your own data path. These files are separate from the database.
   Once the files are in place, give `1654:1654` read and write access to both
   folders and their contents, and access through the parent paths. On Linux,
   for these **new** directories only, use
   `sudo chown -R 1654:1654 /srv/logic-manager/config /srv/logic-manager/data`.
   On a NAS, set the equivalent access in its permissions screen. Keep access
   private. Do not copy the files into an existing installation.
6. Set up your first administrator. Keep Docker's port mapping on `127.0.0.1`
   and disconnect any proxy or tunnel while you do this. Temporarily change
   `Authentication__Mode` in `compose.yaml` to `bypass`, run `docker compose up -d`,
   and visit `http://localhost:8080/account/admin` on that machine. Follow the
   [authentication guide](AUTHENTICATION.md#bootstrap-and-recovery) to create an
   admin and save the temporary password privately. Stop the service, restore
   `Authentication__Mode: required`, then run `docker compose up -d` again.
   Required mode needs browser-facing HTTPS. See below before connecting any
   proxy or tunnel. Never expose the app in bypass mode.

## Deployment contract

Copy `tools/container/compose.example.yaml` and adapt it privately. Set
`LOGIC_MANAGER_IMAGE` to the approved versioned image or preferably recorded digest,
`LOGIC_MANAGER_CONFIG` to an existing private host config directory and
`LOGIC_MANAGER_DATA` to an existing private host data directory. TrueNAS examples map
`applications/<app-name>/config` to `/config` and `applications/<app-name>/mount` to
`/data`; select actual pool paths/ACLs, not literal examples. The image uses nonroot
`app` UID/GID 1654; pre-provision writable bind directories with suitable ownership/ACLs.
No startup recursive chown, privilege escalation or host-directory creation is supplied.
The sample fixes that UID/GID and binds only host loopback port 8080. Docker Desktop
must use Linux containers. ARM emulation, ARM images, multi-replica writers, network
filesystems and orchestrator/ingress management are outside this delivery.

The image keeps content root `/app`, selects `/config` with
`LogicManager__ConfigDirectory` and `/data` with `Storage__DataRoot`. Keep those paths
unchanged and mount at them; do not override their equivalents inside appsettings or
other host configuration. The entrypoint rejects changed locator/root environment
values and arguments; application env overrides its file values. `/config` is private
and writable, `/data` writable for SQLite sidecars and all retained stores. The image
does not use a portable marker, launch a desktop browser, or force localhost:5000;
its normal server listener is HTTP 8080, configurable with native hosting settings.

The image's unmounted/named-volume config default explicitly selects `required`,
forwarding off and publishing off. A bind mount hides that default: supply your
private `/config/appsettings.json` or explicit mode environment. The Compose sample
explicitly selects `Authentication__Mode=required`, which overrides the file. There
is **no server bypass default**, no preinstalled account, and no generated credential.
Required mode with no usable account stays locked. For initial account setup, follow
the [authentication guide](AUTHENTICATION.md#bootstrap-and-recovery): temporarily use
explicit bypass **only on an isolated, operator-only listener**, create the Admin and
capture its one-time credential, then deliberately recreate in required mode retaining
the same `/data`. Never expose bypass through a tunnel or to untrusted clients.
Required-mode Secure cookies need browser-facing HTTPS. Configure a host-preserving
HTTPS proxy/tunnel, exact immediate trusted proxy IPs and explicit AllowedHosts under
the guide's HTTP hosting contract; do not enable broad forwarded-header trust. The
loopback HTTP example alone is not a usable authenticated public deployment.

## Database, upgrades and recovery

The image does not contain a logic database and does not download one. Put the
downloaded `silksong-rando-logic.db` in your new data folder before startup. If the
database is missing, startup stops instead of creating an empty installation.
Existing data is never overwritten with a downloaded copy. Normal application
migrations and store failure behavior still apply; an invalid database is not
replaced automatically.

If you see an interrupted-initialization error, preserve the whole data directory
and inspect it offline. For an existing installation, restore complete consistent
state privately. Do not delete a recovery marker or overwrite the database to
get past the error. Only one process may use a data folder at a time.

Updates replace image/application bytes, never Config or retained `/data`. Back up
configuration, SQLite-consistent primary/accounts DBs, keys, images and the complete
private Publishing recovery workspace before an operator-controlled upgrade. The
build hook does **not** back up deployed roots. Preserve matching old image and private
backups for rollback; a binary-only rollback cannot undo forward migrations. Missing
permissions, corruption, unavailable mounts and account/key failures are not permission
to reseed, downgrade auth or automatically reset storage. See the authentication guide
for full backup, native cookie cutover and recovery limitations.

## Check your setup

- On disposable mounts, verify fresh setup with the separately provisioned database; missing/wrong
  permissions, nonempty missing DB and interrupted-copy marker fail safely; existing
  DB/assets/config/accounts/keys/history/logs/Publishing are not overwritten on restart
  or image upgrade. Exercise operator recovery without touching a live installation.
- Verify isolated Admin bootstrap, required-mode HTTPS login/password replacement,
  proxy host/scheme/trust rejection, media/native image capture and optional documentation
  rendering/publishing readiness, and persistence across operator-controlled recreate.
