[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src/Zapret.Desktop/Zapret.Desktop.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version
if (-not $Version) { $Version = $projectVersion }
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Version -ne $projectVersion) {
    throw "Release version must match the Desktop project version ($projectVersion)."
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'dist' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$packageName = "ZapretDesktop-v$Version-win-x64"
$zipPath = Join-Path $outputRoot "$packageName.zip"
$hashPath = "$zipPath.sha256"
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $hashPath)) {
    throw 'A release artifact already exists. Choose an empty output directory to avoid replacing reviewed artifacts.'
}
$publishRoot = Join-Path $outputRoot ('.publish-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($publishRoot) | Out-Null

try {
    if (-not $SkipTests) {
        & dotnet test (Join-Path $repositoryRoot 'ZapretDesktop.slnx') --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
    }
    & dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishProfile=WinX64 -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false --output $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

    # Export only the runtime distribution files; no local flags, backups, test history or settings.
    $distributionSource = Join-Path $repositoryRoot 'zapret'
    $distributionTarget = Join-Path $publishRoot 'zapret'
    if (Test-Path -LiteralPath $distributionTarget) {
        $resolvedDistribution = [IO.Path]::GetFullPath($distributionTarget)
        if (-not $resolvedDistribution.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Distribution publish path escaped the temporary output directory.'
        }
        Remove-Item -LiteralPath $resolvedDistribution -Recurse -Force
    }
    [IO.Directory]::CreateDirectory($distributionTarget) | Out-Null
    # Use the repository's tracked runtime files, never recursively package arbitrary
    # user files or reports (test output may have any filename).
    $trackedFiles = @(& git -C $repositoryRoot -c core.quotepath=false ls-files -- zapret)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the distribution packaging manifest from Git.' }
    foreach ($trackedPath in $trackedFiles) {
        $relative = $trackedPath.Substring('zapret/'.Length).Replace('\', '/')
        $runtime = $relative -match '^(?:service\.bat|general[^/]*\.bat)$' -or
            $relative -match '^bin/[^/]+\.(?:exe|dll|sys|bin)$' -or
            $relative -match '^lists/(?:list-general|list-google|list-exclude|ipset-all|ipset-exclude)(?:-user)?\.txt$' -or
            $relative -in @('utils/test zapret.ps1', 'utils/targets.txt', '.service/version.txt')
        if (-not $runtime -or $relative -eq 'bin/TgWsProxy_windows.exe') { continue }
        $file = Get-Item -LiteralPath (Join-Path $distributionSource $relative)
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked package file: $($file.FullName)" }
        $destination = Join-Path $distributionTarget $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    foreach ($name in @('list-general-user.txt', 'list-exclude-user.txt')) {
        [IO.File]::WriteAllText((Join-Path $distributionTarget "lists/$name"), '', [Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText((Join-Path $distributionTarget 'lists/ipset-exclude-user.txt'), "203.0.113.113/32`n", [Text.UTF8Encoding]::new($false))

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $publishRoot 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $publishRoot 'THIRD_PARTY_NOTICES.md') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $publishRoot 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs/releases/v$Version.md") -Destination (Join-Path $publishRoot 'RELEASE_NOTES.md') -Force
    [IO.Directory]::CreateDirectory((Join-Path $publishRoot 'licenses')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $distributionSource 'LICENSE.txt') -Destination (Join-Path $publishRoot 'licenses/Zapret-LICENSE.txt') -Force
    foreach ($license in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'licenses') -File) {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $publishRoot 'licenses') -Force
    }
    foreach ($required in @('Zapret.Desktop.exe', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md', 'RELEASE_NOTES.md',
        'licenses/Zapret-LICENSE.txt', 'licenses/WinDivert-LICENSE.txt', 'licenses/Avalonia-LICENSE.md',
        'licenses/DotNet-LICENSE.txt', 'licenses/Inter-OFL.txt', 'licenses/FluentIcons-LICENSE.txt',
        'licenses/Cygwin-LICENSE.txt', 'licenses/LGPL-3.0.txt', 'licenses/GPL-3.0.txt',
        'zapret/service.bat', 'zapret/bin/winws.exe', 'zapret/bin/WinDivert.dll', 'zapret/bin/WinDivert64.sys',
        'zapret/lists/list-general.txt', 'zapret/utils/test zapret.ps1')) {
        $path = Join-Path $publishRoot $required
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Release package is missing $required."
        }
    }
    if (@(Get-ChildItem -LiteralPath $distributionTarget -Filter 'general*.bat' -File).Count -eq 0) { throw 'No zapret strategies were packaged.' }
    foreach ($unwanted in Get-ChildItem -LiteralPath $publishRoot -File -Recurse -Force) {
        if ($unwanted.Extension -eq '.pdb' -or $unwanted.Name -like '*.enabled' -or $unwanted.Name -like '*.backup' -or
            $unwanted.Name -like '*.test-backup.txt' -or $unwanted.Name -like '*.tmp' -or $unwanted.Name -like '*.log' -or
            $unwanted.Name -eq 'settings.json' -or $unwanted.Name -eq 'history.json') {
            throw "Runtime or debug data entered the release: $($unwanted.FullName)"
        }
    }
    if ((Get-Item -LiteralPath (Join-Path $publishRoot 'Zapret.Desktop.exe')).Length -lt 10MB) {
        throw 'The Desktop executable is unexpectedly small for a self-contained single-file build.'
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($archive.Entries.Count -lt 10 -or -not ($archive.Entries.FullName -contains 'Zapret.Desktop.exe') -or
            -not ($archive.Entries.FullName -contains 'zapret/bin/winws.exe')) { throw 'Release ZIP verification failed.' }
    }
    finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($hashPath, "$hash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Verified release: $zipPath"
    Write-Output "SHA-256: $hash"
}
catch {
    if (Test-Path -LiteralPath $zipPath -PathType Leaf) { Remove-Item -LiteralPath $zipPath -Force }
    if (Test-Path -LiteralPath $hashPath -PathType Leaf) { Remove-Item -LiteralPath $hashPath -Force }
    throw
}
finally {
    $resolvedPublish = [IO.Path]::GetFullPath($publishRoot)
    if (-not $resolvedPublish.StartsWith($outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary publish cleanup path escaped the release directory.' }
    if (Test-Path -LiteralPath $resolvedPublish) { Remove-Item -LiteralPath $resolvedPublish -Recurse -Force }
}
