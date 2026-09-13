[CmdletBinding()]
param(
    [ValidateSet('all', 'win-x64', 'linux-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier = 'all'
)

$ErrorActionPreference = 'Stop'

if ($RuntimeIdentifier -eq 'all') {
    foreach ($target in @('win-x64', 'linux-x64', 'osx-arm64')) {
        & $PSCommandPath -RuntimeIdentifier $target
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
$sourceData = Join-Path $projectDirectory 'Data'
$sourceDatabase = Join-Path $sourceData 'silksong-rando-logic.db'
$sourceScenes = Join-Path $sourceData 'scenes'
$sourceSceneSource = Join-Path $sourceData 'scene-source'
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
    & dotnet publish $projectFile --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $applicationRoot
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
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

    $packageData = Join-Path $applicationRoot 'data'
    if (Test-Path -LiteralPath $packageData) {
        Remove-Item -LiteralPath $packageData -Recurse -Force
    }

    New-Item -ItemType Directory -Path $packageData -Force | Out-Null
    Copy-Item -LiteralPath $snapshot.FullName -Destination (Join-Path $packageData 'silksong-rando-logic.db')
    Copy-Item -LiteralPath $sourceScenes -Destination (Join-Path $packageData 'scenes') -Recurse
    $packageSceneSource = Join-Path $packageData 'scene-source'
    New-Item -ItemType Directory -Path $packageSceneSource -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceSceneSource -Force |
        Where-Object Name -ne 'room-map-hd.png' |
        Copy-Item -Destination $packageSceneSource -Recurse
    New-Item -ItemType File -Path (Join-Path $applicationRoot 'portable-release.marker') -Force | Out-Null

    Copy-Item -LiteralPath $sourceReadme -Destination (Join-Path $packageRoot 'README.md')
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
