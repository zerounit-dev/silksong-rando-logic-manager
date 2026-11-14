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

$image = "${ImageRepository}:$Version"
$context = Join-Path ([IO.Path]::GetTempPath()) ('silksong-container-' + [Guid]::NewGuid().ToString('N'))
try {
    # Reuse the complete existing privacy/consistent-backup/managed-binary checks.
    # This also regenerates the ordinary Linux standalone folder and archive.
    & (Join-Path $PSScriptRoot 'PackagePortableRelease.ps1') -RuntimeIdentifier linux-x64 -PrivateRoots $PrivateRoots
    $portable = Join-Path $repositoryRoot 'releases/linux-x64/Silksong Rando Logic Manager'
    foreach ($entry in Get-ChildItem -LiteralPath $portable -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Container input must not contain symbolic links or junctions: $($entry.FullName)"
        }
    }
    New-Item -ItemType Directory -Path $context -Force | Out-Null
    $app = Join-Path $context 'app'
    Copy-Item -LiteralPath $portable -Destination $app -Recurse
    # Container is a normal server, never a portable desktop startup.
    Remove-Item -LiteralPath (Join-Path $app 'portable-release.marker')
    Remove-Item -LiteralPath (Join-Path $app 'Config') -Recurse
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
    if (Test-Path -LiteralPath $context) { Remove-Item -LiteralPath $context -Recurse -Force }
}
