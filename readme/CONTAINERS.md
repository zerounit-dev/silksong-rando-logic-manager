# Linux AMD64 container release and deployment

The container is an additional application delivery format. The Windows x64 ZIP,
Linux x64 tarball and macOS ARM64 tarball remain unchanged. There is no Scene Dumper
or generated-logic publication in this workflow.

## Build and optional GHCR publication (release owner only)

1. Select the application release version and release notes. Invoke the helper from
   the separate development suite root (version illustrative):

   ```powershell
   ./logic-manager/project/tools/SetReleaseVersion.ps1 -Version 1.2.3
   ```

   It updates only the application `Version` text in development
   `project/Directory.Build.props`, preserving its condition and unrelated bytes,
   and reports the old/new values after read-back verification. It does not choose
   a version, build, commit, tag or publish. Run it **before** the normal
   `publish-logic-manager-release.ps1` source mirror/review.
   That mirror generates sanitized required-mode settings and a consistent public
   SQLite seed with allowlisted scene assets. It does not build or push an image.
   Declare any custom in-source private roots to the mirror's `-PrivateRoots`.
   Inspect destination status and ignored files before replacement. Use `-Force` only
   when every overwritten file is confirmed disposable generated output. Preserve
   source changes, unknown files, private data, `.git`, and current-release outputs
   needed for resumption. Do not rewrite a published GitHub release or tag.
   The actual source release origin is
   `https://github.com/zerounit-dev/silksong-rando-logic-manager.git`, so the image
   repository is `ghcr.io/zerounit-dev/silksong-rando-logic-manager` unless explicitly
   overridden. Image address and intended public visibility follow that public
   application repository. Verify actual GHCR
   visibility during publication rather than assuming a first push is public.
2. Review/commit the curated source repository as usual. Never edit mirrored files
   directly. Use that same independent source release checkout for all three standalone
   packages and this image. Keep credentials out of source and the public logic seed.
3. To publish the container image, authenticate Docker to GHCR outside
   the script using the owner's approved credential method and minimum package-write
   access; never pass credentials as script/build arguments or commit them. With
   PowerShell, .NET 10 SDK and a running Docker Linux engine with Buildx available,
   invoke from the **curated source release repository** (version illustrative):

   ```powershell
   ./tools/PackagePortableRelease.ps1 -RuntimeIdentifier win-x64
   ./tools/PackagePortableRelease.ps1 -RuntimeIdentifier osx-arm64
   ./tools/PackageContainerRelease.ps1 -ImageRepository ghcr.io/zerounit-dev/silksong-rando-logic-manager -Version 1.2.3 -Push
   ```

   The image version must match the declared application `Version` in
   `Directory.Build.props`; the image gets that exact tag, never automatic `latest`.
   This order builds Linux standalone staging/archive exactly once through container
   packaging. No prior local image build/load is required. Container packaging reuses the existing
   Linux portable builder's full sanitized staging, consistent SQLite snapshot and
   no-PDB/builder-path checks, also regenerating its ordinary standalone folder/archive.
   The portable boundary also validates SDK-transformed startup HTML, all entry scripts,
   imports/preload and shipped native endpoint targets before archiving. Container copying
   does not select or rewrite startup HTML independently.
   It then removes only its **copied** portable marker/bypass configuration, moves the
   copied public Storage into an immutable image seed and generates required-mode
   defaults. The existing standalone artifacts retain their marker, config and behavior.
   The Docker context is a disposable sanitized directory, not the source tree; its
   deny-by-default `.dockerignore` admits only application files, seed, generated
   config, entrypoint and license. No SDK/build tool is shipped in the runtime image.
   The existing build-backup hook stays enabled and protects the release checkout's
   source seed, not deployed state. Temporary context is deleted on success/failure.
   For local-only image packaging, omit `-Push` **instead of** the final command above:

   ```powershell
   ./tools/PackageContainerRelease.ps1 -ImageRepository ghcr.io/zerounit-dev/silksong-rando-logic-manager -Version 1.2.3
   ```

   This default builds/loads a local `linux/amd64` image only, without starting a
   container, logging in or pushing. For a standalone-only release, the existing
   `./tools/PackagePortableRelease.ps1 -RuntimeIdentifier all` option remains unchanged.
4. `-Push` builds and pushes that version tag instead of loading it locally. It prints
   the immutable `ghcr.io/...@sha256:...` reference from Buildx publication metadata.
   Record that digest with the release evidence. Verify/set GHCR public visibility and
   check pull access as part of the authorized application release; a first push does
   not promise a public package. On failed/uncertain push or missing
   digest, inspect the existing version tag remotely before retrying. No automatic
   retry, tag deletion, rollback, login or GitHub Release is performed. Do not reuse
   a published version for different bytes; base-image updates require a deliberate
   rebuilt release/digest. The Ubuntu .NET runtime-deps base includes standard native
   runtime libraries; libgomp1 supports the shipped ImageMagick native library.

`-PrivateRoots` is forwarded to portable staging for any additional declared private
roots in the curated checkout. It is not secret discovery. Host Config/Storage never
enter recursively: only the existing consistent public DB and exact asset allowlist
are reconstructed. Accounts, keys, history/logs/backups, private Publishing recovery,
image staging, SQLite sidecars, rollback material and source master stay excluded.

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

## First-install seed, upgrades and recovery

Before starting the app, the nonroot entrypoint copies only the immutable public seed
and images into a **genuinely empty** `/data`. It never seeds Config or private accounts
and never merges new-release images into existing state. If the primary DB already
exists as a regular file, all existing state is left untouched; normal application
migrations/initialization and store failure behavior then apply. An invalid or zero-byte
existing DB is not repaired/replaced by this script. Nonempty storage missing its primary
DB fails rather than initializing a different installation over partial state.

Seed copying creates `/data/.container-seed-in-progress` first. Failed/interrupted
copying leaves that marker and stops subsequent startup, including when part of the DB
has already moved. Preserve the whole directory and inspect it offline. Only for a
confirmed new installation with no user-authored state, an operator may deliberately
choose a **different empty directory** and retry. For an existing installation restore
complete consistent state privately. Do not just delete the marker, overwrite a DB,
or repeatedly restart to hide partial initialization. Only one process may own a data
root; the initialization marker is not a general multi-instance database lock.

Updates replace image/application bytes, never Config or retained `/data`. Back up
configuration, SQLite-consistent primary/accounts DBs, keys, images and the complete
private Publishing recovery workspace before an operator-controlled upgrade. The
build hook does **not** back up deployed roots. Preserve matching old image and private
backups for rollback; a binary-only rollback cannot undo forward migrations. Missing
permissions, corruption, unavailable mounts and account/key failures are not permission
to reseed, downgrade auth or automatically reset storage. See the authentication guide
for full backup, native cookie cutover and recovery limitations.

## Release and deployment validation

- Build all three standalone variants once using the release sequence above and verify
  unchanged archive/launch behavior. Build Linux AMD64 image (load locally or push as
  authorized; no mandatory load-before-push); inspect its context,
  layers, nonroot UID and absence of private state/portable marker/bypass settings.
- On disposable mounts, verify empty-root seed and image availability; missing/wrong
  permissions, nonempty missing DB and interrupted-copy marker fail safely; existing
  DB/assets/config/accounts/keys/history/logs/Publishing are not overwritten on restart
  or image upgrade. Exercise operator recovery without touching a live installation.
- Verify isolated Admin bootstrap, required-mode HTTPS login/password replacement,
  proxy host/scheme/trust rejection, media/native image capture and optional documentation
  rendering/publishing readiness, and persistence across operator-controlled recreate.
- During an authorized application release, verify version tag/platform, printed digest
  against GHCR, intended visibility and pull/deploy by digest. Source parsing does not
  establish native dependencies, Docker build success or deployment security.
