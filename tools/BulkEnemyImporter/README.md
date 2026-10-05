# BI1 — one-off operational Enemy importer

Real .NET 10 console operator utility; does not start the web application, read
appsettings, migrate or seed SQLite. Do not use it as a test/runtime harness.
Use the settled contract in `documentation/BULK_ENEMY_INGEST_PLAN.md` (relative to
Logic Manager). **Implementation/review is not permission to apply.** Review the
concrete report before approving its machine-artifact SHA256. No automatic apply.

## Build (project only, Release)

From `logic-manager/project` (or the published source checkout root), redirect
outputs outside the running app. Application
build remains separately required. Both builds preserve the application reference's
database-backup-on-build hook; these backups are not the apply backup.

```powershell
$buildOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("BI1-build-" + [Guid]::NewGuid().ToString("N"))
dotnet build "tools/BulkEnemyImporter/BulkEnemyImporter.csproj" -c Release -o $buildOutput
```

Run the built DLL, **not** `dotnet run` (which builds again). Commands below are
examples, not execution authorization. Make an operator directory outside both the
dump input tree and the database's Data directory. Plan/report/receipt paths must
be new, distinct paths; directories must exist. Do not use filesystem links to
alias artifacts to source data. Preserve artifacts and backups for recovery.

These PowerShell examples use the runtime's temporary directory, not a particular
user profile. You may instead choose durable build/operator directories outside
the input tree and live database directory. Retain the operator directory and its
backups; do not allow temporary-directory cleanup to remove recovery evidence.
Keep the same `$buildOutput` and `$operator` values through plan and approved apply;
in a new shell, set them to the directories you previously selected.

## Read-only plan

```powershell
$tool = Join-Path $buildOutput "BulkEnemyImporter.dll"
$input = (Resolve-Path "..\references\scenedump").Path
$db = (Resolve-Path "Silksong Rando Logic Manager\Silksong Rando Logic Manager\Data\silksong-rando-logic.db").Path
$operator = Join-Path ([System.IO.Path]::GetTempPath()) ("BI1-operator-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $operator | Out-Null
dotnet $tool plan --input $input --database $db --plan "$operator\plan.json" --report "$operator\report.txt"
```

The relative input path above assumes the original source checkout's scene-dump
folder. In a published checkout, select your own existing scene-dump directory
instead (for example, `$input = (Resolve-Path $env:BI1_SCENE_DUMPS).Path` after
setting that environment variable). The database example selects the checkout's
Data database; replace `$db` with the exact intended existing database path when
operating elsewhere. Neither input nor database is inferred by the utility.

Only recursive case-insensitive `.sceneReady.json` suffixes are parsed, serially
with the ordinary 256 MiB parser limit, classification, SI1/SN1/SW1 defaults. Other
files are listed as excluded, not parsed. Embedded names match trimmed
OrdinalIgnoreCase active room IDs; names of files never select rooms. Distinct
captures claiming one scene conflict even if identical. Duplicate room IDs,
unmatched captures, blank room IDs, fatal inputs and parse warnings are distinct.
Valid zero-enemy scenes are covered and write nothing. Fatal inputs without a
reliable scene name block missing-room placeholders. Unhashable admitted input
also prevents apply because exact input binding cannot be verified.

Read **all** coverage, blockers, warnings, instructions and exclusions. Report has
exact machine-artifact digest; machine JSON is authoritative for exact values.
Enemy Creates initialize normal annotations/type/world predicates; Updates change
only imported identity/world/local XYZ, not authored fields. All active target-room
checks share SI1 identity matching. Counts use the full current hierarchy; naming
allocates selected Enemies only. Inactive/repeated actors remain. No new rooms,
dimensions/group/image-stale modifications or image output. Existing resolver can
touch navigation metadata outside the target room.

Missing-room placeholders are ordinary active Unknown checks named exactly
`import enemies`, TODO, explicitly unverified, blank Requirements/null validation,
no identity/coordinates and annotation disabled. Unique required-shape namesake is
reused without edits; multiple/differing active namesakes block that room. Archives
do not veto creation. Covered-room placeholders are never removed or modified.

## Approved apply — separate execution gate

The operator must prevent concurrent database editing **throughout** apply. The
tool does not stop the app, globally lock out writers, or prove that editing has
ceased. Input files must also remain stable. Supply the approved exact digest:

```powershell
dotnet $tool apply --input $input --database $db --plan "$operator\plan.json" --approved-sha256 "<explicitly approved SHA256>" --backup-directory "$operator" --receipt "$operator\receipt.jsonl" --no-concurrent-editing
```

Preflight checks the approved artifact, explicit target/input paths, regenerates
the complete plan against a read-only consistent logical database snapshot, and
rejects any difference. It then uses SQLite `BackupDatabase` on the actual target,
reserves a distinct timestamped backup, verifies read-only `integrity_check` and
logical-baseline equality, and records backup path/file SHA256. Backup failure
aborts all writes. Failed/partial backups are retained, not restored or deleted.
Immediately before the first unit transaction, after backup/receipt preparation,
the workflow rechecks all admitted hashes/inventory and the complete logical DB.

One scene uses the existing importer transaction with strict Enemy-only existing
room admission and metadata preservation; each placeholder uses its own active
room/shape-revalidated transaction. Independent failures continue without retry.
Blockers produce non-success even when admitted work commits. No whole-batch
rollback is promised. Ctrl+C stops further work; current uncommitted work rolls
back on transaction disposal, prior commits remain. Parsing/SQLite backup may need
to return before cancellation is observed.

Receipt is append-only JSONL: session start, verified backup, unit start **before**
transaction, confirmed committed result **after** return, failures/cancellation,
and final actual totals. Created IDs and executed Update target IDs are recorded;
no-op metadata Update still counts an executed Update instruction. Unit exceptions
are marked commit-not-confirmed, not fabricated zero counts. Receipt write/flush
failure stops all further writes and reports uncertainty; receipt/SQLite commit
are not atomic. A torn final line or started unit without outcome requires durable
database inspection, not guessing. Diagnostic exception details go to stderr and
failure receipts. Exit 0 means no blockers/failures; 1 means non-success; 130 means
cancellation (an inability to record cancellation may instead exit 1).

## Recovery and fingerprint encoding

Never blindly rerun an approved stale plan: earlier commits change its baseline,
and repeated Enemy Creates can duplicate rows. There is no retry, resume, restore,
rollback or delete command. Inspect the retained receipt/backup/durable database;
generate a fresh plan to new artifact paths and obtain a fresh operator review.
After any external edits, regenerate even if the DB main-file bytes appear equal.

Logical fingerprint `BI1-logical-sqlite-v1` includes persistent user_version,
application_id, encoding, schema type/name/table/SQL (not physical root pages), all
table names/column names and every typed cell including internal/history tables.
BinaryWriter encoding: little-endian integers, length-prefixed UTF8 strings;
cell tag 0 null, 1 int64, 2 IEEE754 double bits, 3 text, 4 length/blob bytes. Schema
sorts SQLite BINARY; tables sort ordinal; encoded rows sort bytewise retaining
multiplicity. Row count/length boundaries are hashed with SHA256. All reads share
one deferred read transaction; transient SQLite/WAL/SHM bytes are excluded.
Parser GUIDs are never stored in plans: input hash + preorder source ordinal/path/
name/identity safely remap instructions to freshly parsed object GUIDs at apply.

Manual B1–B6 acceptance and independent full-unit review are required. Source/build
evidence does not establish runtime SQLite, filesystem, cancellation or UI behavior.
