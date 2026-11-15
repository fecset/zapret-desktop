param([switch]$Light, [switch]$Compact, [switch]$Large)
$ErrorActionPreference = 'Stop'

function Test-PreviewDirectory {
    param([string]$Directory, [switch]$InspectChildren)
    try {
        # Check every ancestor as well as the output itself: a junction above an
        # otherwise ordinary directory must not redirect retention outside the repo.
        $current = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Directory))
        while ($null -ne $current) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
            $current = $current.Parent
        }
        if ($InspectChildren) {
            $pending = [Collections.Generic.Queue[string]]::new()
            $pending.Enqueue($Directory)
            while ($pending.Count -gt 0) {
                foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
                    if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
                }
            }
        }
        return $true
    }
    catch { Write-Verbose "Cannot safely inspect preview directory '$Directory': $_"; return $false }
}

function Get-CompletedPreviewOutput {
    param([string]$PreviewRoot, [string]$Directory)
    try {
        $full = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::GetDirectoryName($full) -ne $PreviewRoot -or [IO.Path]::GetFileName($full) -notmatch '^[0-9a-f]{32}$') { return }
        if (-not (Test-PreviewDirectory -Directory $full -InspectChildren)) { return }
        $markerPath = Join-Path $full '.preview-owner.json'
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { return }
        $markerFile = Get-Item -LiteralPath $markerPath -Force
        if ($markerFile.Length -eq 0 -or $markerFile.Length -gt 4096 -or
            ($markerFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        $ownerId = 0
        $started = 0L
        if ($marker.Version -ne 1 -or -not [int]::TryParse([string]$marker.ProcessId, [ref]$ownerId) -or $ownerId -le 0 -or
            -not [long]::TryParse([string]$marker.ProcessStartTicks, [ref]$started) -or $started -le 0) { return }
        $owner = $null
        try {
            try { $owner = [Diagnostics.Process]::GetProcessById($ownerId) }
            catch [ArgumentException] { Write-Verbose "Preview owner $ownerId has exited." }
            if ($null -ne $owner -and $owner.StartTime.ToUniversalTime().Ticks -eq $started) { return }
        }
        finally { if ($null -ne $owner) { $owner.Dispose() } }

        # Also preserve a marked directory whose executable was reopened by hand.
        # If a process path cannot be inspected, fail closed and retain the output.
        $inUse = $false
        foreach ($preview in [Diagnostics.Process]::GetProcessesByName('Zapret.Desktop.Tests')) {
            try {
                $executable = $preview.MainModule.FileName
                if (-not $executable -or $executable.StartsWith($full + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $inUse = $true }
            }
            catch { Write-Verbose "Cannot inspect a running preview: $_"; $inUse = $true }
            finally { $preview.Dispose() }
        }
        if (-not $inUse) { [pscustomobject]@{ Directory = $full; ProcessId = $ownerId; ProcessStartTicks = $started } }
    }
    catch { Write-Verbose "Keeping preview output '$Directory' because its ownership cannot be verified: $_" }
}

function Remove-CompletedPreviewOutputs {
    param([string]$ProjectRoot, [ValidateRange(0, 50)][int]$KeepCompleted = 3)
    $previewRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'TestResults/engineering-preview')).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not (Test-PreviewDirectory -Directory $previewRoot)) { return }
    try {
        $completed = @(foreach ($directory in Get-ChildItem -LiteralPath $previewRoot -Directory -Force) {
            Get-CompletedPreviewOutput -PreviewRoot $previewRoot -Directory $directory.FullName
        })
    }
    catch { Write-Warning "Cannot list preview outputs for retention: $_"; return }
    foreach ($entry in $completed | Sort-Object ProcessStartTicks -Descending | Select-Object -Skip $KeepCompleted) {
        # Recheck identity, liveness and the entire directory tree immediately
        # before deletion. Unmarked legacy outputs are deliberately never claimed.
        $current = Get-CompletedPreviewOutput -PreviewRoot $previewRoot -Directory $entry.Directory
        if ($null -eq $current -or $current.ProcessId -ne $entry.ProcessId -or $current.ProcessStartTicks -ne $entry.ProcessStartTicks) { continue }
        try { Remove-Item -LiteralPath $current.Directory -Recurse -Force }
        catch { Write-Warning "Cannot remove completed preview '$($current.Directory)': $_" }
    }
}

# Dot-sourcing exposes only the retention helper, without building or launching UI.
if ($MyInvocation.InvocationName -eq '.') { return }
if ($Compact -and $Large) { throw 'Choose either Compact or Large.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previewOutput = Join-Path $projectRoot ('TestResults/engineering-preview/' + [Guid]::NewGuid().ToString('N'))
dotnet build (Join-Path $projectRoot 'tests/Zapret.Desktop.Tests/Zapret.Desktop.Tests.csproj') -c Release -p:UiPreview=true -p:CopyDebugSymbolFilesFromPackages=false -o $previewOutput
if ($LASTEXITCODE -ne 0) { throw 'UI preview build failed' }
$previewArguments = @()
if ($Light) { $previewArguments += '--light' }
if ($Compact) { $previewArguments += '--compact' }
if ($Large) { $previewArguments += '--large' }
$start = @{ FilePath = (Join-Path $previewOutput 'Zapret.Desktop.Tests.exe'); WindowStyle = 'Hidden'; PassThru = $true
    WorkingDirectory = $projectRoot
    RedirectStandardOutput = (Join-Path $previewOutput 'stdout.log')
    RedirectStandardError = (Join-Path $previewOutput 'stderr.log') }
if ($previewArguments.Count) { $start.ArgumentList = $previewArguments }
$previewProcess = Start-Process @start
$previewProcessId = $previewProcess.Id
try {
    $owner = @{ Version = 1; ProcessId = $previewProcessId; ProcessStartTicks = $previewProcess.StartTime.ToUniversalTime().Ticks.ToString() }
    [IO.File]::WriteAllText((Join-Path $previewOutput '.preview-owner.json'), ($owner | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
catch { Write-Warning "Preview started, but its output will be retained because ownership could not be recorded: $_" }
finally { $previewProcess.Dispose() }
Remove-CompletedPreviewOutputs -ProjectRoot $projectRoot
Write-Output "State gallery PID: $previewProcessId. Uses temporary files and mocked services."
Write-Output "Preview executable: $(Join-Path $previewOutput 'Zapret.Desktop.Tests.exe')"
