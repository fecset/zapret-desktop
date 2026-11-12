using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Zapret.Core;

namespace Zapret.Infrastructure;

/// <summary>A local helper performs the app directory transaction only after the current process exits.</summary>
internal static class DesktopUpdateHelper
{
    internal static async Task<DesktopUpdateSchedule> ScheduleAsync(ManagedUpdatePlan plan,
        IReadOnlyDictionary<string, string> hashes, string applicationDirectory, string workspace,
        int currentProcessId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop updates require Windows.");
        // Elevated code and its manifest must never be loaded from a user-writable directory.
        // Program Files provides a protected parent; create the job with its ACL atomically.
        var jobRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ZapretDesktop-update-" + Guid.NewGuid().ToString("N"));
        UpdateFiles.RequireDistinctTrees(applicationDirectory, jobRoot);
        UpdateFiles.RequireDistinctTrees(applicationDirectory, workspace);
        UpdateFiles.RequireDistinctTrees(workspace, jobRoot);
        if (!Path.GetFullPath(plan.PreparedDirectory).Equals(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase))
            UpdateFiles.RequireChild(workspace, plan.PreparedDirectory);
        UpdateFiles.RequireNoLinks(jobRoot);
        new DirectoryInfo(jobRoot).Create(JobSecurity());
        var helper = Path.Combine(jobRoot, "apply-update.ps1");
        var manifest = Path.Combine(jobRoot, "update.json");
        var log = Path.Combine(jobRoot, "result.log");
        var ready = Path.Combine(jobRoot, "ready");
        var backup = applicationDirectory + ".backup-" + plan.Id;
        var replacement = applicationDirectory + ".update-" + plan.Id;
        if (Directory.Exists(backup) || Directory.Exists(replacement)) throw new IOException("The Desktop update transaction path already exists.");
        using var process = Process.GetProcessById(currentProcessId);
        var job = new
        {
            ProcessId = currentProcessId, ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Source = plan.PreparedDirectory, Workspace = workspace, Target = applicationDirectory, Replacement = replacement, Backup = backup,
            Log = log, Ready = ready, Hashes = hashes, Relaunch = true, RefreshIcons = true, Version = plan.Version
        };
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(job), new UTF8Encoding(false), ct);
        // Windows PowerShell 5.1 needs a BOM to read localized script literals as UTF-8.
        await File.WriteAllTextAsync(helper, Script, new UTF8Encoding(true), ct);
        ct.ThrowIfCancellationRequested();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var start = new ProcessStartInfo(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            // A process holds its native working directory open until it exits.
            // Keep that handle outside the job so the script can remove its own directory.
            WorkingDirectory = Path.GetDirectoryName(jobRoot)!
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-ManifestPath", manifest })
            start.ArgumentList.Add(argument);
        using var updater = Process.Start(start) ?? throw new InvalidOperationException("Desktop update helper could not start.");
        // A ready handshake catches policy/script failures before the UI exits. Once ready, the
        // transaction belongs to the helper, and it times out without touching a running app.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(ready))
        {
            if (updater.HasExited || DateTime.UtcNow >= deadline)
            {
                if (!updater.HasExited) updater.Kill();
                throw new InvalidOperationException("Desktop update helper failed to initialize. Inspect " + log);
            }
            await Task.Delay(100, CancellationToken.None);
        }
        return new(helper, log, backup,
            $"Подготовлено обновление Desktop {plan.Version}. Приложение закроется, установит обновление и запустится снова. Журнал при ошибке: {log}");
    }

    internal static DirectorySecurity JobSecurity()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static DirectorySecurity InstallationSecurity()
    {
        var security = JobSecurity();
        // The EXE must be readable before Program.Main requests elevation.
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal const string Script = """
        param([Parameter(Mandatory = $true)][string]$ManifestPath)
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        $job = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $movedOld = $false
        $movedNew = $false
        $ownerExited = $false
        function Write-Outcome([bool]$success, [string]$detail) {
            $path = $job.Target + '.update-result.json'
            Assert-NoLinks $path
            $version = $job.PSObject.Properties['Version']
            $value = if ($null -ne $version) { [string]$version.Value } else { '' }
            $result = @{ Success = $success; Version = $value; Detail = $detail } | ConvertTo-Json -Compress
            [IO.File]::WriteAllText($path, $result)
        }
        function Start-Desktop {
            $relaunch = $job.PSObject.Properties['Relaunch']
            if ($ownerExited -and $null -ne $relaunch -and $relaunch.Value) {
                Assert-NoLinks (Join-Path $job.Target 'Zapret.Desktop.exe')
                Start-Process -FilePath (Join-Path $job.Target 'Zapret.Desktop.exe') -WorkingDirectory $job.Target
            }
        }
        function Refresh-DesktopIcon([string]$executable) {
            # Win32 LONG is 32-bit on both Windows x86 and x64. Item pointers are
            # caller-owned UTF-16 strings and remain valid until FLUSH completes.
            Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ZapretDesktopShell { [DllImport("shell32.dll", ExactSpelling = true)] public static extern void SHChangeNotify([MarshalAs(UnmanagedType.I4)] int eventId, uint flags, IntPtr item1, IntPtr item2); }'
            [ZapretDesktopShell]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)
            $item = [Runtime.InteropServices.Marshal]::StringToCoTaskMemUni($executable)
            try { [ZapretDesktopShell]::SHChangeNotify(0x2000, 0x1005, $item, [IntPtr]::Zero) }
            finally { [Runtime.InteropServices.Marshal]::FreeCoTaskMem($item) }
        }
        function Assert-NoLinks([string]$path) {
            $current = [IO.Path]::GetFullPath($path)
            while ($current) {
                if (Test-Path -LiteralPath $current) {
                    $item = Get-Item -LiteralPath $current -Force
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked update path: $current" }
                }
                $current = [IO.Path]::GetDirectoryName($current)
            }
        }
        function Get-SafeFiles([string]$root) {
            Assert-NoLinks $root
            $pending = New-Object 'System.Collections.Generic.Stack[string]'
            $pending.Push($root)
            while ($pending.Count -gt 0) {
                $folder = $pending.Pop()
                foreach ($entry in Get-ChildItem -LiteralPath $folder -Force) {
                    Assert-NoLinks $entry.FullName
                    if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
                    else { $entry }
                }
            }
        }
        function Get-Relative([string]$root, [string]$path) {
            $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
            $full = [IO.Path]::GetFullPath($path)
            if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path outside update directory' }
            return $full.Substring($prefix.Length)
        }
        function Assert-DistinctTrees([string]$first, [string]$second) {
            $left = [IO.Path]::GetFullPath($first).TrimEnd('\') + '\'
            $right = [IO.Path]::GetFullPath($second).TrimEnd('\') + '\'
            if ($left.StartsWith($right, [StringComparison]::OrdinalIgnoreCase) -or
                $right.StartsWith($left, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Overlapping update cleanup directories'
            }
        }
        function Remove-TemporaryTree([string]$path) {
            Assert-NoLinks $path
            @(Get-SafeFiles $path) | Out-Null
            Remove-Item -LiteralPath $path -Recurse -Force
        }
        function Assert-Manifest([string]$root) {
            $files = @(Get-SafeFiles $root)
            if ($files.Count -ne @($job.Hashes.PSObject.Properties).Count) { throw 'Update file count changed' }
            foreach ($file in $files) {
                $relative = Get-Relative $root $file.FullName
                $expected = $job.Hashes.PSObject.Properties[$relative]
                if ($null -eq $expected) { throw "Unexpected update file: $relative" }
                $inputStream = [IO.File]::OpenRead($file.FullName)
                $hasher = [Security.Cryptography.SHA256]::Create()
                try { $actual = [BitConverter]::ToString($hasher.ComputeHash($inputStream)).Replace('-', '').ToLowerInvariant() }
                finally { $hasher.Dispose(); $inputStream.Dispose() }
                if ($actual -ne $expected.Value) { throw "Update hash mismatch: $relative" }
            }
        }
        function Get-InstallSecurity {
                $security = New-Object Security.AccessControl.DirectorySecurity
                $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
                $security.SetOwner($administrators)
                $security.SetAccessRuleProtection($true, $false)
                foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) {
                    $ruleIdentity = New-Object Security.Principal.SecurityIdentifier($sid)
                    $rule = New-Object Security.AccessControl.FileSystemAccessRule($ruleIdentity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                    $security.AddAccessRule($rule)
                }
                $readIdentity = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
                $readRule = New-Object Security.AccessControl.FileSystemAccessRule($readIdentity, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                $security.AddAccessRule($readRule)
                return $security
        }
        function Copy-SafeTree([string]$source, [string]$target, [bool]$preserve) {
            Assert-NoLinks $target
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = New-Object Security.Principal.WindowsPrincipal($identity)
            if (-not $preserve -and $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                [IO.Directory]::CreateDirectory($target, (Get-InstallSecurity)) | Out-Null
            }
            else { [IO.Directory]::CreateDirectory($target) | Out-Null }
            foreach ($file in Get-SafeFiles $source) {
                $relative = Get-Relative $source $file.FullName
                $destination = Join-Path $target $relative
                Assert-NoLinks $destination
                $name = $file.Name
                $editableList = $relative -match '^(?:zapret\\)?lists\\[^\\]+\.txt$'
                $user = $editableList -or $name -like '*-user.txt' -or $name -like '*.enabled' -or $name -like '*.backup' -or
                    $name -eq 'ipset-all.txt' -or $name -like 'ACTIVE_*.bin' -or $name -eq 'settings.json'
                if ($preserve -and -not $user -and (Test-Path -LiteralPath $destination -PathType Leaf)) { continue }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                [IO.File]::Copy($file.FullName, $destination, $preserve)
            }
        }
        try {
            $jobRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
            $workspaceFull = [IO.Path]::GetFullPath($job.Workspace).TrimEnd('\')
            foreach ($path in @($job.Source, $job.Target, $job.Replacement, $job.Backup, $job.Log, $job.Ready, $jobRoot, $workspaceFull)) { Assert-NoLinks $path }
            $targetFull = [IO.Path]::GetFullPath($job.Target).TrimEnd('\')
            $sourceFull = [IO.Path]::GetFullPath($job.Source).TrimEnd('\')
            $backupFull = [IO.Path]::GetFullPath($job.Backup).TrimEnd('\')
            if ([IO.Path]::GetFileName($jobRoot) -notmatch '^ZapretDesktop-update-[0-9a-fA-F]{32}$' -or
                [IO.Path]::GetFullPath($ManifestPath) -ne (Join-Path $jobRoot 'update.json') -or
                [IO.Path]::GetFullPath($job.Log) -ne (Join-Path $jobRoot 'result.log') -or
                [IO.Path]::GetFullPath($job.Ready) -ne (Join-Path $jobRoot 'ready')) {
                throw 'Unexpected Desktop helper directory'
            }
            if ([IO.Path]::GetFileName($workspaceFull) -notmatch '^[0-9a-fA-F]{32}$' -or
                ($sourceFull -ne $workspaceFull -and -not $sourceFull.StartsWith($workspaceFull + '\', [StringComparison]::OrdinalIgnoreCase))) {
                throw 'Unexpected Desktop staging directory'
            }
            foreach ($tree in @($job.Target, $job.Replacement, $job.Backup, $workspaceFull)) { Assert-DistinctTrees $jobRoot $tree }
            foreach ($tree in @($job.Target, $job.Replacement, $job.Backup)) { Assert-DistinctTrees $workspaceFull $tree }
            $backupPrefix = $targetFull + '.backup-'
            if (-not $backupFull.StartsWith($backupPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                $backupFull.Substring($backupPrefix.Length) -notmatch '^[0-9a-fA-F]{32}$') {
                throw 'Unexpected Desktop rollback directory'
            }
            if ($targetFull -eq [IO.Path]::GetPathRoot($targetFull).TrimEnd('\') -or
                $sourceFull.StartsWith($targetFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
                $targetFull.StartsWith($sourceFull + '\', [StringComparison]::OrdinalIgnoreCase) -or $sourceFull -eq $targetFull) {
                throw 'Unsafe Desktop update directory relationship'
            }
            if ([IO.Path]::GetDirectoryName($job.Target) -ne [IO.Path]::GetDirectoryName($job.Replacement) -or
                [IO.Path]::GetDirectoryName($job.Target) -ne [IO.Path]::GetDirectoryName($job.Backup)) { throw 'Transaction paths must be siblings' }
            if ((Test-Path -LiteralPath $job.Backup) -or (Test-Path -LiteralPath $job.Replacement)) { throw 'Transaction path already exists' }
            Assert-Manifest $job.Source
            [IO.File]::WriteAllText($job.Ready, 'ready')
            $deadline = [DateTime]::UtcNow.AddMinutes(10)
            while ($true) {
                $owner = Get-Process -Id $job.ProcessId -ErrorAction SilentlyContinue
                if ($null -eq $owner) { break }
                if ($owner.StartTime.ToUniversalTime().Ticks.ToString() -ne $job.ProcessStartTicks) { break }
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Desktop did not exit; update was not applied' }
                Start-Sleep -Milliseconds 250
            }
            $ownerExited = $true
            Assert-Manifest $job.Source
            Copy-SafeTree $job.Source $job.Replacement $false
            Assert-Manifest $job.Replacement
            # Desktop and zapret have independent update actions. Keep the installed
            # distribution as a complete tree, never merge binaries from two versions.
            $installedDistribution = Join-Path $job.Target 'zapret'
            if (Test-Path -LiteralPath $installedDistribution -PathType Container) {
                @(Get-SafeFiles $installedDistribution) | Out-Null
                $bundledDistribution = Join-Path $job.Replacement 'zapret'
                Get-Relative $job.Replacement $bundledDistribution | Out-Null
                @(Get-SafeFiles $bundledDistribution) | Out-Null
                Remove-Item -LiteralPath $bundledDistribution -Recurse -Force
                Copy-SafeTree $installedDistribution $bundledDistribution $false
            }
            Copy-SafeTree $job.Target $job.Replacement $true
            # Recheck the complete trees immediately before the directory transaction.
            @(Get-SafeFiles $job.Target) | Out-Null
            @(Get-SafeFiles $job.Replacement) | Out-Null
            Assert-NoLinks $job.Backup
            [IO.Directory]::Move($job.Target, $job.Backup)
            $movedOld = $true
            [IO.Directory]::Move($job.Replacement, $job.Target)
            $movedNew = $true
            [IO.File]::WriteAllText($job.Log, 'SUCCESS: Desktop updated.')
            Write-Outcome $true 'Приложение обновлено. Установленный zapret и пользовательские данные сохранены.'
            # The install is committed. Cleanup errors must not roll back a working
            # version, and a successful update must not retain the old app folder.
            $backupCleanupFailed = $false
            try {
                Assert-NoLinks $backupFull
                @(Get-SafeFiles $backupFull) | Out-Null
                Remove-Item -LiteralPath $backupFull -Recurse -Force
            }
            catch {
                $backupCleanupFailed = $true
                $cleanupFailure = $_.Exception.Message
                try {
                    [IO.File]::AppendAllText($job.Log, [Environment]::NewLine + "Cannot remove temporary rollback directory: $backupFull. $cleanupFailure")
                    Write-Outcome $true ("Приложение обновлено, но временную папку предыдущей версии не удалось удалить: $backupFull. $cleanupFailure")
                }
                catch { Write-Warning "Cannot record cleanup failure: $($_.Exception.Message)" }
            }
            $refreshIcons = $job.PSObject.Properties['RefreshIcons']
            if ($null -ne $refreshIcons -and $refreshIcons.Value) {
                try { Refresh-DesktopIcon (Join-Path $job.Target 'Zapret.Desktop.exe') }
                catch { Write-Warning "Cannot refresh Desktop icon: $($_.Exception.Message)" }
            }
            # The script is already parsed, and the native working directory is outside
            # the job. Remove only this update's validated workspace and helper directory.
            # Failed installs keep these files and their log for diagnosis/retry.
            try {
                Remove-TemporaryTree $workspaceFull
                if (-not $backupCleanupFailed) { Remove-TemporaryTree $jobRoot }
            }
            catch {
                $cleanupFailure = $_.Exception.Message
                try {
                    Write-Outcome $true ("Приложение обновлено, но временные файлы обновления не удалось удалить: " + $cleanupFailure)
                    [IO.File]::AppendAllText($job.Log, "`r`nCannot remove temporary update files: $cleanupFailure")
                }
                catch { Write-Warning "Cannot record cleanup failure: $($_.Exception.Message)" }
            }
            try { Start-Desktop }
            catch {
                $relaunchFailure = $_.Exception.Message
                try {
                    Write-Outcome $true ("Обновление установлено, но автоматический запуск приложения не удался: " + $relaunchFailure)
                    Write-Warning "Relaunch failed: $relaunchFailure"
                }
                catch { Write-Warning "Cannot record relaunch failure: $($_.Exception.Message)" }
            }
            exit 0
        }
        catch {
            $failure = $_.Exception.Message
            try {
                if ($movedOld -and -not $movedNew) {
                    Assert-NoLinks $job.Backup
                    Assert-NoLinks $job.Target
                    [IO.Directory]::Move($job.Backup, $job.Target)
                }
                elseif ($movedOld -and $movedNew) {
                    # Even a failure writing the success log leaves the previous app recoverable.
                    $failed = $job.Target + '.failed-' + [Guid]::NewGuid().ToString('N')
                    Assert-NoLinks $job.Target
                    Assert-NoLinks $job.Backup
                    Assert-NoLinks $failed
                    [IO.Directory]::Move($job.Target, $failed)
                    [IO.Directory]::Move($job.Backup, $job.Target)
                }
            }
            catch { $failure += "; ROLLBACK FAILED: $($_.Exception.Message). Backup: $($job.Backup)" }
            try { [IO.File]::WriteAllText($job.Log, "FAILED: $failure") }
            catch { $failure += "; Cannot write installation log: $($_.Exception.Message)" }
            try { Write-Outcome $false ("Не удалось установить обновление. Подробности: " + $failure) }
            catch { Write-Warning "Cannot save update result: $($_.Exception.Message)" }
            try { Start-Desktop }
            catch { Write-Warning "Cannot reopen Desktop: $($_.Exception.Message)" }
            exit 1
        }
        """;
}
