[CmdletBinding()]
param(
    [ValidateSet('all', 'win-x64', 'linux-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier = 'all',
    [string[]] $PrivateRoots = @()
)

$ErrorActionPreference = 'Stop'

if ($RuntimeIdentifier -eq 'all') {
    foreach ($target in @('win-x64', 'linux-x64', 'osx-arm64')) {
        & $PSCommandPath -RuntimeIdentifier $target -PrivateRoots $PrivateRoots
        if ($LASTEXITCODE -ne 0) {
            throw "Portable release build failed for $target with exit code $LASTEXITCODE."
        }
    }

    return
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectDirectory = Join-Path (Join-Path $repositoryRoot 'Silksong Rando Logic Manager') 'Silksong Rando Logic Manager'
$projectFile = Join-Path $projectDirectory 'Silksong Rando Logic Manager.csproj'
$backupProject = Join-Path (Join-Path (Join-Path $repositoryRoot 'tools') 'DatabaseBackup') 'DatabaseBackup.csproj'
$tarballBuilderProject = Join-Path (Join-Path (Join-Path $repositoryRoot 'tools') 'LinuxTarballBuilder') 'LinuxTarballBuilder.csproj'
$sourceData = Join-Path $projectDirectory 'Storage'
$sourceDatabase = Join-Path $sourceData 'silksong-rando-logic.db'
$sourceScenes = Join-Path $sourceData 'scenes'
$sourceSceneSource = Join-Path $sourceData 'scene-source'
$sourceSceneOriginals = Join-Path $sourceData 'scene-originals'
$safePortableConfig = '{"Authentication":{"Mode":"bypass"},"Storage":{"DataRoot":"Storage"},"DocumentationPublishing":{"Enabled":false},"HttpHosting":{"ForwardingEnabled":false}}'
$resolvedPrivateRoots = @(
    foreach ($root in @((Join-Path $projectDirectory 'Config'), (Join-Path $projectDirectory 'Storage')) + $PrivateRoots) {
        if ([string]::IsNullOrWhiteSpace($root) -or $root.IndexOfAny([char[]]'*?;') -ge 0) {
            throw 'PrivateRoots requires literal directory paths, not empty entries, globs or semicolon lists.'
        }
        $candidate = if ([IO.Path]::IsPathRooted($root)) { $root } else { Join-Path $repositoryRoot $root }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { throw 'PrivateRoots entries must be directories, not files.' }
        [IO.Path]::GetFullPath($candidate).TrimEnd('\', '/')
    }
)
# Default Storage exclusion has explicit public seed/image reconstruction below.
# Operator-declared roots must never be reintroduced through that exception.
foreach ($declaredRoot in $PrivateRoots) {
    $root = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($declaredRoot)) { $declaredRoot } else { Join-Path $repositoryRoot $declaredRoot })).TrimEnd('\', '/')
    foreach ($seed in @($sourceDatabase, $sourceScenes, $sourceSceneSource, $sourceSceneOriginals)) {
        $seed = [IO.Path]::GetFullPath($seed).TrimEnd('\', '/')
        if ($root.Equals($seed, [StringComparison]::OrdinalIgnoreCase) -or
            $seed.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $root.StartsWith($seed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'PrivateRoots overlaps deliberately public Storage seed inputs. Choose an uncontaminated public seed before packaging; runtime settings are not changed.'
        }
    }
}
$privatePublishPaths = @(
    $projectRoots = @($repositoryRoot, $projectDirectory) + @(Get-ChildItem -LiteralPath $repositoryRoot -Directory -Filter 'SilksongRandoLogicManager.*' | ForEach-Object FullName)
    foreach ($root in $resolvedPrivateRoots) {
        foreach ($projectRoot in $projectRoots) {
            if ($root.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                $root.Substring($projectRoot.Length + 1)
            }
        }
    }
)
$sourceReadmeDirectory = Join-Path $repositoryRoot 'readme'
$sourceReadme = Join-Path $sourceReadmeDirectory 'README.md'
$sourceLogicGuide = Join-Path $sourceReadmeDirectory 'LOGIC.md'
$repositoryLicense = Join-Path $repositoryRoot 'LICENSE'
$developmentLicense = Join-Path (Split-Path -Parent $repositoryRoot) 'LICENSE'
$sourceLicense = if (Test-Path -LiteralPath $repositoryLicense -PathType Leaf) {
    $repositoryLicense
}
elseif (Test-Path -LiteralPath $developmentLicense -PathType Leaf) {
    $developmentLicense
}
else {
    throw "Required MIT license was not found. Expected either release-root '$repositoryLicense' or development-parent '$developmentLicense'."
}
$releaseDirectory = Join-Path $repositoryRoot 'releases'
$packageRoot = Join-Path $releaseDirectory $RuntimeIdentifier
$applicationRoot = Join-Path $packageRoot 'Silksong Rando Logic Manager'
$archiveExtension = if ($RuntimeIdentifier -eq 'win-x64') { 'zip' } else { 'tar.gz' }
$releaseArchive = Join-Path $releaseDirectory "silksong-rando-logic-manager-$RuntimeIdentifier.$archiveExtension"
$temporarySnapshotDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("silksong-rando-portable-" + [Guid]::NewGuid().ToString('N'))
$temporaryArchive = Join-Path $temporarySnapshotDirectory "silksong-rando-logic-manager-$RuntimeIdentifier.$archiveExtension"

foreach ($requiredPath in @($projectFile, $backupProject, $tarballBuilderProject, $sourceDatabase, $sourceScenes, $sourceSceneSource, $sourceReadme, $sourceLogicGuide, $sourceLicense)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required portable-release source path was not found: $requiredPath"
    }
}

function Assert-StagedPortableOutputSafe {
    param(
        [Parameter(Mandatory = $true)]
        [string] $StagedRoot,

        [Parameter(Mandatory = $true)]
        [string[]] $BuilderPathMarkers
    )

    $expectedAuthenticationConfig = Join-Path $StagedRoot 'Silksong Rando Logic Manager/Config/appsettings.json'
    $configFiles = @(Get-ChildItem -LiteralPath (Split-Path $expectedAuthenticationConfig) -Recurse -File -Force)
    if ($configFiles.Count -ne 1 -or $configFiles[0].FullName -ine $expectedAuthenticationConfig -or
        [System.IO.File]::ReadAllText($configFiles[0].FullName) -cne $safePortableConfig) {
        throw 'Portable output must contain only the generated safe bypass configuration.'
    }
    foreach ($entry in Get-ChildItem -LiteralPath $StagedRoot -Recurse -Force) {
        if ($entry.Name -ieq 'authentication.json' -or $entry.Name -ilike 'accounts.db*' -or
            ($entry.PSIsContainer -and $entry.Name -in @('authentication', 'history', 'logs', 'backups', 'scene-image-staging'))) {
            throw "Private authentication state must never enter a portable artifact: $($entry.FullName)"
        }
    }
    $pdbFiles = @(
        Get-ChildItem -LiteralPath $StagedRoot -Recurse -File -Force |
            Where-Object { $_.Extension.Equals('.pdb', [System.StringComparison]::OrdinalIgnoreCase) }
    )
    if ($pdbFiles.Count -ne 0) {
        throw "Staged portable output contains a prohibited PDB: $($pdbFiles[0].FullName)"
    }

    $pathVariants = @(
        $BuilderPathMarkers |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object {
                $_
                $_.Replace('\', '/')
                $_.Replace('/', '\')
            } |
            Select-Object -Unique
    )
    $decodings = @(
        [pscustomobject]@{ Encoding = [System.Text.Encoding]::UTF8; Offset = 0 },
        [pscustomobject]@{ Encoding = [System.Text.Encoding]::ASCII; Offset = 0 },
        [pscustomobject]@{ Encoding = [System.Text.Encoding]::Unicode; Offset = 0 },
        [pscustomobject]@{ Encoding = [System.Text.Encoding]::Unicode; Offset = 1 },
        [pscustomobject]@{ Encoding = [System.Text.Encoding]::BigEndianUnicode; Offset = 0 },
        [pscustomobject]@{ Encoding = [System.Text.Encoding]::BigEndianUnicode; Offset = 1 }
    )
    $managedFiles = @(
        Get-ChildItem -LiteralPath $StagedRoot -Recurse -File -Force |
            Where-Object {
                $_.Extension.Equals('.dll', [System.StringComparison]::OrdinalIgnoreCase) -or
                $_.Extension.Equals('.exe', [System.StringComparison]::OrdinalIgnoreCase)
            }
    )

    foreach ($managedFile in $managedFiles) {
        $bytes = [System.IO.File]::ReadAllBytes($managedFile.FullName)
        foreach ($decoding in $decodings) {
            $encoding = $decoding.Encoding
            $offset = $decoding.Offset
            if ($bytes.Length -le $offset) {
                continue
            }

            $decoded = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
            foreach ($pathVariant in $pathVariants) {
                if ($decoded.IndexOf($pathVariant, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Staged managed executable contains a builder-specific path in '$($managedFile.FullName)'."
                }
            }
        }
    }
}

try {
    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    # Escape the MSBuild property-list separator for the dotnet command-line parser.
    $privateRootsProperty = $resolvedPrivateRoots -join '%3B'
    & dotnet publish $projectFile --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $applicationRoot "-p:LogicManagerPrivateRoots=$privateRootsProperty" '-p:LogicManagerSanitizedPortable=true'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
    foreach ($relative in $privatePublishPaths) {
        if (Test-Path -LiteralPath (Join-Path $applicationRoot $relative)) {
            throw "Declared private directory entered published output: $relative"
        }
    }

    New-Item -ItemType Directory -Path $temporarySnapshotDirectory -Force | Out-Null
    & dotnet run --project $backupProject --configuration Release -- $sourceDatabase $temporarySnapshotDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "SQLite seed snapshot failed with exit code $LASTEXITCODE."
    }

    $snapshots = @(Get-ChildItem -LiteralPath $temporarySnapshotDirectory -Filter '*.db')
    if ($snapshots.Count -ne 1) {
        throw "SQLite seed snapshot was not created exactly once for $sourceDatabase."
    }

    $snapshot = $snapshots[0]

    # Discard any legacy seed copied by SDK items, then insert only the selected
    # consistent public seed/assets. Never copy Config or the whole Storage root.
    $legacyData = Join-Path $applicationRoot 'data'
    if (Test-Path -LiteralPath $legacyData) { Remove-Item -LiteralPath $legacyData -Recurse -Force }
    $packageData = Join-Path $applicationRoot 'Storage'
    if (Test-Path -LiteralPath $packageData) {
        Remove-Item -LiteralPath $packageData -Recurse -Force
    }

    New-Item -ItemType Directory -Path $packageData -Force | Out-Null
    $packageConfig = Join-Path $applicationRoot 'Config'
    New-Item -ItemType Directory -Path $packageConfig -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $packageConfig 'appsettings.json'), $safePortableConfig, [System.Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $snapshot.FullName -Destination (Join-Path $packageData 'silksong-rando-logic.db')
    foreach ($directory in @('scenes', 'scene-originals', 'scene-source/tiles')) {
        New-Item -ItemType Directory -Path (Join-Path $packageData $directory) -Force | Out-Null
    }
    # The existing public image stores, not arbitrary runtime files or the source master.
    $publicAssetRoots = @($sourceScenes, $sourceSceneSource) + @($sourceSceneOriginals | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
    foreach ($file in Get-ChildItem -LiteralPath $publicAssetRoots -File -Recurse -Force -ErrorAction Stop) {
        $relative = $file.FullName.Substring($sourceData.Length + 1).Replace('\', '/')
        if ($relative -notmatch '^(?:scenes/[0-9a-f-]{36}\.webp|scene-originals/[0-9a-f-]{36}\.image|scene-source/(?:preview\.webp|build\.json|tiles/(?:[0-9]+-[0-9]+\.webp|ATTRIBUTION\.md)))$') { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Public portable asset must not be a reparse point.' }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageData $relative)
    }
    New-Item -ItemType File -Path (Join-Path $applicationRoot 'portable-release.marker') -Force | Out-Null

    if (Test-Path -LiteralPath (Join-Path $packageData 'Publishing')) {
        throw 'Private publishing workspace/configuration must never enter a portable artifact.'
    }
    if (Test-Path -LiteralPath (Join-Path $packageData 'history')) {
        throw 'Private record history must never enter a portable artifact.'
    }

    Copy-Item -LiteralPath $sourceReadme -Destination (Join-Path $packageRoot 'README.md')
    Copy-Item -LiteralPath (Join-Path (Split-Path $sourceReadme) 'AUTHENTICATION.md') -Destination (Join-Path $packageRoot 'AUTHENTICATION.md')
    Copy-Item -LiteralPath $sourceLogicGuide -Destination (Join-Path $packageRoot 'LOGIC.md')
    Copy-Item -LiteralPath $sourceLicense -Destination (Join-Path $packageRoot 'LICENSE')

    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $repositoryParent = Split-Path -Parent $repositoryRoot
    $workspaceRoot = if ((Split-Path -Leaf $repositoryRoot) -eq 'project' -and (Split-Path -Leaf $repositoryParent) -eq 'logic-manager') {
        Split-Path -Parent $repositoryParent
    }
    else {
        $repositoryParent
    }
    $builderPathMarkers = @($userProfile, $repositoryRoot, $workspaceRoot)

    if ($RuntimeIdentifier -eq 'win-x64') {
        [System.IO.File]::WriteAllLines(
            (Join-Path $packageRoot 'Launch Silksong Rando Logic Manager.cmd'),
            @('@echo off', 'pushd "%~dp0Silksong Rando Logic Manager"', 'start "" "Silksong_Rando_Logic_Manager.exe"', 'popd'),
            [System.Text.UTF8Encoding]::new($false))

        Assert-StagedPortableOutputSafe -StagedRoot $packageRoot -BuilderPathMarkers $builderPathMarkers
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $temporaryArchive)
    }
    else {
        $linuxHost = Join-Path $applicationRoot 'Silksong_Rando_Logic_Manager'
        $linuxLauncher = Join-Path $packageRoot 'Launch Silksong Rando Logic Manager.sh'
        [System.IO.File]::WriteAllText(
            $linuxLauncher,
            (@('#!/usr/bin/env sh', 'set -eu', 'script_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)', 'cd "$script_directory/Silksong Rando Logic Manager"', 'exec ./Silksong_Rando_Logic_Manager "$@"') -join "`n"),
            [System.Text.UTF8Encoding]::new($false))
        Assert-StagedPortableOutputSafe -StagedRoot $packageRoot -BuilderPathMarkers $builderPathMarkers
        & dotnet run --project $tarballBuilderProject --configuration Release -- $packageRoot $temporaryArchive $linuxHost $linuxLauncher
        if ($LASTEXITCODE -ne 0) {
            throw "Linux tarball creation failed with exit code $LASTEXITCODE."
        }
    }
    Move-Item -LiteralPath $temporaryArchive -Destination $releaseArchive -Force

    Write-Host "Portable release folder created: $packageRoot"
    Write-Host "Portable release archive created: $releaseArchive"
}
finally {
    if (Test-Path -LiteralPath $temporarySnapshotDirectory) {
        Remove-Item -LiteralPath $temporarySnapshotDirectory -Recurse -Force
    }
}
