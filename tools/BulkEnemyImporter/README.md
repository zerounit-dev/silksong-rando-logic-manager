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
with the ordinary 256 MiB parser limit, classification, SI1/SN1/SW1 defaults and
EPN1 identity and EPN3 governing-condition/default successors. Other
files are listed as excluded, not parsed. Embedded names match trimmed
OrdinalIgnoreCase active room IDs; names of files never select rooms. Distinct
captures claiming one scene conflict even if identical. Duplicate room IDs,
unmatched captures, blank room IDs, fatal inputs and parse warnings are distinct.
Valid zero-enemy scenes are covered and write nothing. Fatal inputs without a
reliable scene name block missing-room placeholders. Unhashable admitted input
also prevents apply because exact input binding cannot be verified.

Read **all** coverage, blockers, warnings, instructions and exclusions. Report has
exact machine-artifact digest; machine JSON is authoritative for exact values.
Enemy Creates initialize normal annotations/type and shared Requirements defaults; Updates change
only imported identity/world/local XYZ, not authored fields. All active target-room
checks share SI1 identity matching. Counts use the full current hierarchy; naming
allocates selected Enemies only. Inactive/repeated actors remain. No new rooms,
dimensions/group/image-stale modifications or image output. Existing resolver can
touch navigation metadata outside the target room.

EPN1 (`documentation/ENEMY_PATH_AND_SPAWN_NOTES_IMPLEMENTATION_PLAN.md`) changes
Enemy game-ID metadata to the full root-to-object named hierarchy (no scene), e.g.
`"Pilgrims"[0]/"Pilgrim Folder"[0]/"Pilgrim 01"[1]`. Every segment has a zero-based
exact-name sibling occurrence index in original parsed structural order. Names,
including `(Clone)`, quotes, slashes and brackets, remain literal with no escaping
inside this opaque string. Normal JSON/report string encoding is transport only;
do not parse this metadata as a path. No stable uniqueness promise, old-ID backfill
or legacy-name fallback. SI1 trimmed OrdinalIgnoreCase collision-safe defaults
remain; deliberate Update can reconcile an old row while retaining authored data.

Machine instructions and human reports include actual proposed Create Notes and
retained Update Notes alongside Requirements. New Enemy Creates receive
`Spawn conditions: field=true, Quest name accepted=true, ...` for finite non-world
governing self/ancestor Boolean/quest expected-value facts; no printable facts means
blank condition Notes. The list is documentation, not AND/OR or a gameplay expression.
Four simple gates retain strict guards/polarity; direct TestGameObjectActivator
zero/multi Bool groups and selected Boolean quest checks support explicit-null unused
sides and verified external targets. Composite opposite criteria count as presence,
not individually inverted facts; the known pure single-Bool inverse is preserved.
Ordering is direct/nearest-to-outer, then controller source/component order, with
exact field/value pairs deduplicated and opposite values retained. Exact
`blackThreadWorld` is omitted from Notes; SW1 proof remains unchanged.
Unsupported criteria retain presence/incompleteness, not guessed facts or absence.
No unresolved placeholder, FSM interpreter, catalogue mapping, new warnings or authored-field
changes on Updates.

EPN4 adds a separate names-only `FSM Boolean flags: name, otherName` section after
the Spawn section, separated by LF; empty sections are omitted. Guarded finite
literal reads on self/actual ancestors count as documentary context (including
exact `blackThreadWorld`, omitted from printing), not proven governing requirements.
They prevent the no-context Invalid default; writes/malformed new reads do not.
Canonical instructions/replan/reparse bind the exact combined Notes as before.
No branch interpretation, authored repair or PLAN/APPLY authorization is implied.

EPN3 (`documentation/ENEMY_GOVERNING_CONDITIONS_IMPLEMENTATION_PLAN.md`) supersedes
EPN2: reliable actor hierarchy=false plus reliable self/ancestor self=false gets exactly
`Invalid` ONLY with no governing condition or relevant uncertainty on the whole chain.
Notes are `Disabled; no activation found.` for self, otherwise `Disabled ancestor; no
activation found.`, alone, never reason plus conditions. Suppression/world/quest and
unsupported criterion presence prevents Invalid, even for a separately disabled child.
Otherwise retain SW1's exact proven world phrase or blank with available finite Notes.
Any known reliable supplied activity contradiction on the chain suppresses the diagnostic;
missing/unknown flags do not invent causes/contradictions. Active unconditional actors,
SW1 world proof/indication and all authored Update fields remain unchanged. Invalid
is freeform text with normal catalogue-dependent validation, not a new enum/atom or
proof game activation is impossible. No actor removal/backfill/FSM/composite inference.

EPN6 removes the unrequested EPN5 persistence Notes section. Shared Notes joins
only nonempty Spawn then FSM sections by LF. Exact attached PersistentBoolItem on
self/actual ancestors still supplies context for either value; direct scalar
validation retains incompleteness when contents are blank/missing/malformed.
Persistence-only context produces blank Notes, not Invalid. Reliable no-context disabled
defaults now additionally require no persistence context/incompleteness. Shared
proposal/replan/reparse/Create strings agree; retained Updates are not repaired.
See `documentation/ENEMY_PERSISTENCE_STATE_CONTRACT.md` and its printing successor
`documentation/ENEMY_PERSISTENCE_NOTES_REMOVAL_PLAN.md`; no PLAN/APPLY is authorized.

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

Notes are part of the canonical approval-bound instruction bytes. Old approved
artifacts do not authorize EPN1 IDs or EPN3 defaults: obtain a fresh plan and separate
concrete APPLY approval. Existing canonical-artifact/replan comparison rejects
stale content; there is no upgrade, compatibility replay or automatic execution.
The completed historical BI1 operation is not authorization for a new operation.

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

Manual B1–B6, EPN1 E1–E7 and EPN2 I1–I6 acceptance and independent full-unit review
are required. Approval of a report authorizes only its separately approved operation,
not acceptance of new application behavior. Source/build
evidence does not establish runtime SQLite, filesystem, cancellation or UI behavior.
