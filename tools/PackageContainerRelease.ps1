[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^ghcr\.io/[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)+$')]
    [string] $ImageRepository,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string[]] $PrivateRoots = @(),
    [switch] $Push
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
# Run only in the independently generated source release checkout, not project/.
foreach ($required in @('.git', 'LICENSE', 'README.md', 'Directory.Build.props')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $required))) {
        throw "Use the curated source release repository. Missing release-root entry: $required"
    }
}
if (Test-Path -LiteralPath (Join-Path $repositoryRoot 'AGENTS.md')) {
    throw 'Container packaging must use the curated source release repository, not the development workspace.'
}
[xml] $properties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$declaredVersion = @($properties.Project.PropertyGroup.Version | Where-Object { $null -ne $_ })
if ($declaredVersion.Count -ne 1 -or $declaredVersion[0].InnerText -cne $Version) {
    throw 'Version must match the approved application Version in Directory.Build.props before source mirroring.'
}
if ($Version.Length -gt 128) { throw 'Version exceeds the Docker tag length limit.' }
Get-Command docker -ErrorAction Stop | Out-Null
& docker buildx version
if ($LASTEXITCODE -ne 0) { throw 'Docker Buildx is required.' }

function Test-RealContainerWorkspaceDirectory([string] $Path) {
    # Walk root to leaf so no path probe goes through an unchecked ancestor.
    $pending = [Collections.Generic.Stack[string]]::new()
    for ($component = $Path; $component; $component = [IO.Path]::GetDirectoryName($component)) {
        $pending.Push($component)
    }
    while ($pending.Count) {
        $component = $pending.Pop()
        try {
            $attributes = [IO.File]::GetAttributes($component)
        }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            if ($pending.Count -eq 0) { return $false } # Only the workspace itself may be absent.
            throw
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
            throw "Container workspace and its ancestors must be real directories, not files or reparse points: $component"
        }
    }
    return $true
}

function Assert-ContainerWorkspaceHasNoReparsePoints([string] $Path) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        $entry = Get-Item -LiteralPath $pending.Pop() -Force
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Container workspace must not contain symbolic links or junctions: $($entry.FullName)"
        }
        if ($entry.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) { $pending.Push($child.FullName) }
        }
    }
}

$image = "${ImageRepository}:$Version"
$workspace = [IO.Path]::GetFullPath([IO.Path]::Combine([IO.Path]::GetTempPath(), ('silksong-container-' + [Guid]::NewGuid().ToString('N'))))
if (Test-RealContainerWorkspaceDirectory $workspace) { throw "Container workspace already exists: $workspace" }
$stage = Join-Path $workspace 'stage'
$context = Join-Path $workspace 'context'
New-Item -ItemType Directory -Path $workspace | Out-Null
try {
    # Run every staging check without writing a standalone archive. Docker sees
    # only the sibling context, never the portable stage or its bypass config.
    & (Join-Path $PSScriptRoot 'PackagePortableRelease.ps1') -RuntimeIdentifier linux-x64 -PrivateRoots $PrivateRoots -OutputRoot $stage -SkipArchive
    if (-not (Test-RealContainerWorkspaceDirectory $workspace)) { throw "Container workspace was not found: $workspace" }
    $portable = Join-Path $stage 'linux-x64/Silksong Rando Logic Manager'
    Assert-ContainerWorkspaceHasNoReparsePoints $workspace
    New-Item -ItemType Directory -Path $context | Out-Null
    $app = Join-Path $context 'app'
    Copy-Item -LiteralPath $portable -Destination $app -Recurse
    # Container is a normal server, never a portable desktop startup.
    Remove-Item -LiteralPath (Join-Path $app 'portable-release.marker')
    Remove-Item -LiteralPath (Join-Path $app 'Config') -Recurse
    # The validated stage's Storage contains approved scene assets only.
    Move-Item -LiteralPath (Join-Path $app 'Storage') -Destination (Join-Path $context 'seed')
    $config = Join-Path $context 'config'
    New-Item -ItemType Directory -Path $config | Out-Null
    [IO.File]::WriteAllText((Join-Path $config 'appsettings.json'),
        '{"Authentication":{"Mode":"required"},"DocumentationPublishing":{"Enabled":false},"HttpHosting":{"ForwardingEnabled":false}}',
        [Text.UTF8Encoding]::new($false))
    foreach ($name in @('Dockerfile', '.dockerignore', 'entrypoint.sh')) {
        # Normalize scripts to LF even in a Windows checkout.
        $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "container/$name")).Replace("`r`n", "`n")
        [IO.File]::WriteAllText((Join-Path $context $name), $text, [Text.UTF8Encoding]::new($false))
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $context 'LICENSE')
    foreach ($file in Get-ChildItem -LiteralPath $context -File -Recurse -Force) {
        if ($file.Name -ilike 'silksong-rando-logic.db*' -or $file.Name -match '(?i)\.(db|sqlite3?)(?:-(wal|shm|journal))?$') {
            throw 'SQLite databases and sidecars must never enter the Docker build context.'
        }
    }
    $metadata = Join-Path $context 'build-metadata.json'
    $output = if ($Push) { '--push' } else { '--load' }
    & docker buildx build --platform linux/amd64 --tag $image --build-arg "RELEASE_VERSION=$Version" --metadata-file $metadata $output $context
    if ($LASTEXITCODE -ne 0) {
        throw 'Container build/publication failed. A push may have reached GHCR; inspect the version tag before retrying. No automatic rollback or retry is attempted.'
    }
    if ($Push) {
        $result = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        $digest = $result.'containerimage.digest'
        if ($digest -notmatch '^sha256:[0-9a-f]{64}$') {
            throw "Push completed but its digest was not reported. Inspect GHCR for $image before retrying."
        }
        Write-Host "Published: $image"
        Write-Host "Immutable reference: ${ImageRepository}@$digest"
    }
    else {
        Write-Host "Local image loaded: $image (not pushed; no container started)"
    }
}
finally {
    if (Test-RealContainerWorkspaceDirectory $workspace) {
        # Do not recursively delete through a link, including on partial-stage failure.
        Assert-ContainerWorkspaceHasNoReparsePoints $workspace
        Remove-Item -LiteralPath $workspace -Recurse -Force
    }
}
