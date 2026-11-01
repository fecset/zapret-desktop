param([switch]$Light, [switch]$Compact, [switch]$Large)
$ErrorActionPreference = 'Stop'
if ($Compact -and $Large) { throw 'Choose either Compact or Large.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$previewOutput = Join-Path $projectRoot ('TestResults/engineering-preview/' + [Guid]::NewGuid().ToString('N'))
dotnet build (Join-Path $projectRoot 'tests/Zapret.Desktop.Tests/Zapret.Desktop.Tests.csproj') -c Release -p:UiPreview=true -o $previewOutput
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
Write-Output "State gallery PID: $($previewProcess.Id). Uses temporary files and mocked services."
Write-Output "Preview executable: $(Join-Path $previewOutput 'Zapret.Desktop.Tests.exe')"
