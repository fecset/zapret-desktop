using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class ManagedUpdateService : IManagedUpdateService
{
    public const string IpSetSource = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
    public const string HostsSource = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";
    private const string HostsBegin = "# BEGIN Zapret Desktop managed hosts";
    private const string HostsEnd = "# END Zapret Desktop managed hosts";
    private readonly IZapretDistribution distribution;
    private readonly HttpClient client;
    private readonly string applicationDirectory;
    private readonly string stagingDirectory;
    private readonly string hostsPath;
    private readonly SemaphoreSlim applyLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PreparedUpdate> updates = new();
    private readonly ConcurrentDictionary<string, ManagedUpdateResult> applied = new();
    private readonly ConcurrentDictionary<string, PreparedIpSet> ipsets = new();
    private readonly ConcurrentDictionary<string, PreparedHosts> hosts = new();
    private sealed record PreparedUpdate(ManagedUpdatePlan Plan, IReadOnlyDictionary<string, string> Hashes);
    private sealed record PreparedIpSet(IpSetUpdatePlan Plan, string ActiveHash, string TargetHash);
    private sealed record PreparedHosts(HostsUpdatePreview Preview, string CurrentHash);
    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("html_url")] string Url,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] Asset[] Assets);
    private sealed record Asset([property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("digest")] string? Digest);

    /// <param name="client">Use HttpClientHandler.AllowAutoRedirect=false; redirects are checked individually.</param>
    public ManagedUpdateService(IZapretDistribution distribution, HttpClient client,
        string? applicationDirectory = null, string? stagingDirectory = null, string? hostsPath = null)
    {
        this.distribution = distribution;
        this.client = client;
        this.applicationDirectory = Path.GetFullPath(applicationDirectory ??
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
        this.stagingDirectory = Path.GetFullPath(stagingDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretDesktop", "updates"));
        this.hostsPath = Path.GetFullPath(hostsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts"));
    }

    public Task<ManagedUpdatePlan> PrepareDistributionAsync(CancellationToken cancellationToken = default) =>
        PrepareAsync(ManagedUpdateProduct.Distribution, distribution.Version, cancellationToken);

    public Task<ManagedUpdatePlan> PrepareDesktopAsync(string localVersion, CancellationToken cancellationToken = default) =>
        PrepareAsync(ManagedUpdateProduct.Desktop, localVersion, cancellationToken);

    private async Task<ManagedUpdatePlan> PrepareAsync(ManagedUpdateProduct product, string? localVersion, CancellationToken ct)
    {
        var repository = product == ManagedUpdateProduct.Distribution ? "Flowseal/zapret-discord-youtube" : "fecset/zapret-desktop";
        var metadata = await DownloadAsync($"https://api.github.com/repos/{repository}/releases/latest", 1024 * 1024, false, ct);
        var release = JsonSerializer.Deserialize<Release>(metadata) ?? throw new InvalidDataException("GitHub release metadata is missing.");
        var version = release.Tag?.TrimStart('v', 'V');
        if (release.Draft || release.Prerelease || !Version.TryParse(version, out var latest) ||
            !Regex.IsMatch(release.Tag ?? "", @"^[vV]?\d+(?:\.\d+){1,3}$"))
            throw new InvalidDataException("Only stable, versioned GitHub releases can be installed.");
        if (Version.TryParse(localVersion, out var local) && latest <= local)
            throw new InvalidOperationException("The installed version is already up to date.");
        var releasePrefix = $"https://github.com/{repository}/releases/";
        RequireUrl(release.Url, "github.com", releasePrefix);
        var expectedName = product == ManagedUpdateProduct.Distribution
            ? $"zapret-discord-youtube-{version}.zip" : $"ZapretDesktop-{release.Tag}-win-x64.zip";
        var candidates = (release.Assets ?? []).Where(x => string.Equals(x.Name, expectedName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("The release must contain exactly one supported Windows ZIP asset.");
        var asset = candidates[0];
        RequireUrl(asset.Url, "github.com", $"{releasePrefix}download/{release.Tag}/{asset.Name}", exactPath: true);
        var limit = product == ManagedUpdateProduct.Distribution ? 64L * 1024 * 1024 : 256L * 1024 * 1024;
        if (asset.Size is <= 0 || asset.Size > limit) throw new InvalidDataException("Release asset size exceeds the download limit.");
        if (asset.Digest is null || !Regex.IsMatch(asset.Digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("The release asset does not provide a SHA-256 digest.");
        var content = await DownloadAsync(asset.Url, limit, true, ct);
        var hash = Hash(content);
        if (content.LongLength != asset.Size || !hash.Equals(asset.Digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded release does not match its GitHub size or SHA-256 digest.");
        UpdateFiles.RequireNoLinks(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var id = Guid.NewGuid().ToString("N");
        var workspace = Path.Combine(stagingDirectory, id);
        Directory.CreateDirectory(workspace);
        try
        {
            await ExtractAsync(content, workspace, product == ManagedUpdateProduct.Distribution ? 256L * 1024 * 1024 : 1024L * 1024 * 1024, ct);
            var preparedRoot = FindPackageRoot(workspace, product == ManagedUpdateProduct.Distribution ? "service.bat" : "Zapret.Desktop.exe");
            if (product == ManagedUpdateProduct.Distribution)
            {
                await ValidateDistributionAsync(preparedRoot, ct);
                if (!Version.TryParse(new ZapretDistribution(preparedRoot).Version?.TrimStart('v', 'V'), out var bundled) || bundled != latest)
                    throw new InvalidDataException("The bundled zapret version does not match the selected release.");
            }
            else
            {
                UpdateFiles.RequirePe(Path.Combine(preparedRoot, "Zapret.Desktop.exe"));
                foreach (var required in new[] { "LICENSE", "THIRD_PARTY_NOTICES.md", "licenses/Zapret-LICENSE.txt", "licenses/WinDivert-LICENSE.txt" })
                    UpdateFiles.RequireFile(preparedRoot, required);
                await ValidateDistributionAsync(Path.Combine(preparedRoot, "zapret"), ct);
            }
            var manifest = UpdateFiles.HashTree(preparedRoot, ct);
            var plan = new ManagedUpdatePlan(id, product, version!, release.Url, asset.Url, content.LongLength, hash,
                manifest.Count, preparedRoot, $"{version}: {manifest.Count} файлов, {content.LongLength / (1024d * 1024):F1} МБ. Пользовательские файлы будут сохранены; предыдущая версия останется в резервной копии.");
            updates[id] = new(plan, manifest);
            return plan;
        }
        catch
        {
            UpdateFiles.DeleteTree(workspace);
            throw;
        }
    }

    public async Task<ManagedUpdateResult> ApplyDistributionAsync(ManagedUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        await applyLock.WaitAsync(cancellationToken);
        try
        {
            var prepared = RequirePrepared(plan, ManagedUpdateProduct.Distribution);
            UpdateFiles.VerifyTree(plan.PreparedDirectory, prepared.Hashes, cancellationToken);
            await ValidateDistributionAsync(plan.PreparedDirectory, cancellationToken);
            var target = Path.GetFullPath(distribution.Root);
            var replacement = target + ".update-" + plan.Id;
            var backup = target + ".backup-" + plan.Id;
            UpdateFiles.RequireDistinctTrees(target, plan.PreparedDirectory);
            UpdateFiles.RequireNoLinks(target);
            UpdateFiles.RequireNoLinks(replacement);
            UpdateFiles.RequireNoLinks(backup);
            if (Directory.Exists(replacement) || Directory.Exists(backup)) throw new IOException("The update backup path already exists.");
            try
            {
                UpdateFiles.CopyTree(plan.PreparedDirectory, replacement, cancellationToken);
                UpdateFiles.VerifyTree(replacement, prepared.Hashes, cancellationToken);
                UpdateFiles.PreserveUserFiles(target, replacement, cancellationToken);
                await ValidateDistributionAsync(replacement, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                UpdateFiles.SwapDirectories(target, replacement, backup);
            }
            catch
            {
                if (Directory.Exists(replacement)) UpdateFiles.DeleteTree(replacement);
                throw;
            }
            var result = new ManagedUpdateResult(plan.Id, plan.Version, target, backup,
                $"zapret {plan.Version} применён. Резервная копия: {backup}");
            applied[plan.Id] = result;
            updates.TryRemove(plan.Id, out _);
            return result;
        }
        finally { applyLock.Release(); }
    }

    public async Task RollbackDistributionAsync(ManagedUpdateResult result, CancellationToken cancellationToken = default)
    {
        await applyLock.WaitAsync(cancellationToken);
        try
        {
            if (!applied.TryGetValue(result.Id, out var known) || known != result)
                throw new InvalidOperationException("This rollback does not belong to a completed update in this session.");
            UpdateFiles.RequireNoLinks(result.InstalledDirectory);
            UpdateFiles.RequireNoLinks(result.BackupDirectory);
            if (!Directory.Exists(result.BackupDirectory)) throw new DirectoryNotFoundException("Update backup is missing.");
            UpdateFiles.PreserveUserFiles(result.InstalledDirectory, result.BackupDirectory, cancellationToken, preserveUnknown: false);
            cancellationToken.ThrowIfCancellationRequested();
            UpdateFiles.SwapDirectories(result.InstalledDirectory, result.BackupDirectory,
                result.InstalledDirectory + ".reverted-" + result.Id);
            applied.TryRemove(result.Id, out _);
        }
        finally { applyLock.Release(); }
    }

    public async Task<DesktopUpdateSchedule> ScheduleDesktopUpdateAsync(ManagedUpdatePlan plan, int currentProcessId,
        CancellationToken cancellationToken = default)
    {
        await applyLock.WaitAsync(cancellationToken);
        try
        {
            var prepared = RequirePrepared(plan, ManagedUpdateProduct.Desktop);
            UpdateFiles.VerifyTree(plan.PreparedDirectory, prepared.Hashes, cancellationToken);
            UpdateFiles.RequireDistinctTrees(applicationDirectory, plan.PreparedDirectory);
            UpdateFiles.RequireFile(applicationDirectory, "Zapret.Desktop.exe");
            UpdateFiles.RequireNoLinks(applicationDirectory);
            if (currentProcessId != Environment.ProcessId) throw new InvalidOperationException("Only the current Desktop process can schedule its own update.");
            var schedule = await DesktopUpdateHelper.ScheduleAsync(plan, prepared.Hashes, applicationDirectory,
                currentProcessId, cancellationToken);
            updates.TryRemove(plan.Id, out _);
            return schedule;
        }
        finally { applyLock.Release(); }
    }

    public async Task<IpSetUpdatePlan> PrepareIpSetAsync(CancellationToken cancellationToken = default)
    {
        var data = await DownloadAsync(IpSetSource, 16L * 1024 * 1024, false, cancellationToken);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Decode(data).Replace("\r", "").Split('\n'))
        {
            var entry = line.Split('#', 2)[0].Trim();
            if (entry.Length == 0) continue;
            var parts = entry.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) ||
                parts[0].Any(c => !(char.IsAsciiHexDigit(c) || c is '.' or ':')) ||
                (parts.Length == 2 && (!int.TryParse(parts[1], out var prefix) || prefix < 0 ||
                    prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))))
                throw new InvalidDataException($"Invalid IPSet entry: {entry}");
            entries.Add(address + (parts.Length == 2 ? "/" + parts[1] : ""));
        }
        if (entries.Count == 0) throw new InvalidDataException("The upstream IPSet is empty.");
        var active = Path.Combine(distribution.ListsDirectory, "ipset-all.txt");
        UpdateFiles.RequireNoLinks(active);
        var activeHash = UpdateFiles.HashFileOrMissing(active);
        var mode = await new FilterService(distribution).GetIpSetModeAsync(cancellationToken);
        var target = mode == IpSetMode.Loaded ? active : active + ".backup";
        UpdateFiles.RequireNoLinks(target);
        var content = string.Join('\n', entries) + "\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        var plan = new IpSetUpdatePlan(Guid.NewGuid().ToString("N"), IpSetSource, target, entries.Count,
            bytes.LongLength, Hash(bytes), content,
            $"{entries.Count} IP/CIDR. " + (mode == IpSetMode.Loaded ? "Обновится активный список." : "Обновится резервный список; текущий режим IPSet сохранится."));
        ipsets[plan.Id] = new(plan, activeHash, UpdateFiles.HashFileOrMissing(target));
        return plan;
    }

    public async Task<DataUpdateResult> ApplyIpSetAsync(IpSetUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        await applyLock.WaitAsync(cancellationToken);
        try
        {
            if (!ipsets.TryGetValue(plan.Id, out var known) || known.Plan != plan)
                throw new InvalidOperationException("Prepare the IPSet update in this session before applying it.");
            var active = Path.Combine(distribution.ListsDirectory, "ipset-all.txt");
            UpdateFiles.RequireNoLinks(active);
            UpdateFiles.RequireNoLinks(plan.TargetPath);
            if (known.ActiveHash != UpdateFiles.HashFileOrMissing(active) || known.TargetHash != UpdateFiles.HashFileOrMissing(plan.TargetPath))
                throw new InvalidOperationException("IPSet changed after the preview. Prepare a new update.");
            var backup = await UpdateFiles.ReplaceTextAsync(plan.TargetPath, plan.Content, cancellationToken);
            ipsets.TryRemove(plan.Id, out _);
            return new(plan.TargetPath, backup, "IPSet обновлён; режим фильтра сохранён.");
        }
        finally { applyLock.Release(); }
    }

    public async Task<HostsUpdatePreview> PreviewHostsAsync(CancellationToken cancellationToken = default)
    {
        var data = await DownloadAsync(HostsSource, 1024 * 1024, false, cancellationToken);
        var upstream = Decode(data).Replace("\r", "");
        var count = 0;
        foreach (var line in upstream.Split('\n'))
        {
            var entry = line.Split('#', 2)[0].Trim();
            if (entry.Length == 0) continue;
            var parts = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _) ||
                parts.Skip(1).Any(x => x.Length > 253 || !Regex.IsMatch(x, @"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")))
                throw new InvalidDataException("The upstream hosts file contains an invalid entry.");
            count += parts.Length - 1;
        }
        if (count == 0 || upstream.Contains(HostsBegin, StringComparison.Ordinal) || upstream.Contains(HostsEnd, StringComparison.Ordinal))
            throw new InvalidDataException("The upstream hosts file is empty or contains reserved managed-block markers.");
        UpdateFiles.RequireNoLinks(hostsPath);
        if (File.Exists(hostsPath) && new FileInfo(hostsPath).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The local hosts file exceeds the preview limit.");
        var currentBytes = File.Exists(hostsPath) ? await File.ReadAllBytesAsync(hostsPath, cancellationToken) : [];
        var current = Decode(currentBytes);
        var retained = current;
        var begin = current.IndexOf(HostsBegin, StringComparison.Ordinal);
        var end = current.IndexOf(HostsEnd, StringComparison.Ordinal);
        if ((begin < 0) != (end < 0) || (begin >= 0 && (end < begin ||
            current.IndexOf(HostsBegin, begin + HostsBegin.Length, StringComparison.Ordinal) >= 0 ||
            current.IndexOf(HostsEnd, end + HostsEnd.Length, StringComparison.Ordinal) >= 0)))
            throw new InvalidDataException("The local hosts managed block is malformed; inspect it before updating.");
        if (begin >= 0)
        {
            // Markers must occupy whole lines, otherwise unrelated content could be removed.
            if ((begin > 0 && current[begin - 1] != '\n') || (end > 0 && current[end - 1] != '\n'))
                throw new InvalidDataException("Hosts managed markers must be on separate lines.");
            var afterBegin = begin + HostsBegin.Length;
            if (afterBegin < current.Length && current[afterBegin] is not ('\r' or '\n'))
                throw new InvalidDataException("Hosts managed markers must be on separate lines.");
            var afterEnd = end + HostsEnd.Length;
            if (afterEnd < current.Length && current[afterEnd] is not ('\r' or '\n'))
                throw new InvalidDataException("Hosts managed markers must be on separate lines.");
            if (afterEnd < current.Length && current[afterEnd] == '\r') afterEnd++;
            if (afterEnd < current.Length && current[afterEnd] == '\n') afterEnd++;
            retained = current[..begin] + current[afterEnd..];
        }
        var proposed = retained + (retained.Length > 0 && !retained.EndsWith('\n') ? "\r\n" : "") +
            HostsBegin + "\r\n" + upstream.TrimEnd().Replace("\n", "\r\n") + "\r\n" + HostsEnd + "\r\n";
        var preview = new HostsUpdatePreview(Guid.NewGuid().ToString("N"), HostsSource, hostsPath, current, proposed,
            count, $"{count} сопоставлений hosts. Пользовательские строки сохранены. Применение изменит системный hosts и создаст резервную копию.");
        hosts[preview.Id] = new(preview, File.Exists(hostsPath) ? Hash(currentBytes) : "missing");
        return preview;
    }

    public async Task<DataUpdateResult> ApplyHostsAsync(HostsUpdatePreview preview, CancellationToken cancellationToken = default)
    {
        await applyLock.WaitAsync(cancellationToken);
        try
        {
            if (!hosts.TryGetValue(preview.Id, out var known) || known.Preview != preview)
                throw new InvalidOperationException("Review the hosts preview in this session before applying it.");
            UpdateFiles.RequireNoLinks(hostsPath);
            if (known.CurrentHash != UpdateFiles.HashFileOrMissing(hostsPath))
                throw new InvalidOperationException("The hosts file changed after the preview. Review a new preview before applying it.");
            var backup = await UpdateFiles.ReplaceTextAsync(hostsPath, preview.ProposedContent, cancellationToken);
            hosts.TryRemove(preview.Id, out _);
            return new(hostsPath, backup, "hosts применён. Резервная копия: " + backup);
        }
        finally { applyLock.Release(); }
    }

    private PreparedUpdate RequirePrepared(ManagedUpdatePlan plan, ManagedUpdateProduct product)
    {
        if (plan.Product != product || !updates.TryGetValue(plan.Id, out var prepared) || prepared.Plan != plan)
            throw new InvalidOperationException("Download and review this update in the current session before applying it.");
        return prepared;
    }

    private async Task<byte[]> DownloadAsync(string url, long limit, bool releaseAsset, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var current = new Uri(url);
        var hostsAllowed = releaseAsset ? new[] { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" } : new[] { current.Host };
        for (var hop = 0; hop <= 5; hop++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || current.Port != 443 ||
                !hostsAllowed.Contains(current.Host, StringComparer.OrdinalIgnoreCase) || !string.IsNullOrEmpty(current.UserInfo))
                throw new InvalidDataException("The download redirected outside trusted HTTPS hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("ZapretDesktop/0.2");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var final = response.RequestMessage?.RequestUri ?? current;
            if (final != current)
                throw new InvalidDataException("Automatic HTTP redirects must be disabled for managed updates.");
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (!releaseAsset || hop == 5 || response.Headers.Location is null)
                    throw new InvalidDataException("Redirects are not allowed for fixed sources, or the redirect limit was exceeded.");
                current = new Uri(current, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The download exceeds the allowed size.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            await CopyBoundedAsync(input, output, limit, timeout.Token);
            return output.ToArray();
        }
        throw new InvalidDataException("The download exceeded the redirect limit.");
    }

    private static void RequireUrl(string? value, string host, string expected, bool exactPath = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Untrusted release URL.");
        var expectedUri = new Uri(expected);
        if (exactPath ? uri.AbsolutePath != expectedUri.AbsolutePath : !uri.AbsolutePath.StartsWith(expectedUri.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidDataException("The release URL does not belong to the expected repository and version.");
    }

    private static async Task ExtractAsync(byte[] content, string destination, long expandedLimit, CancellationToken ct)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count is 0 or > 5000) throw new InvalidDataException("The ZIP has an invalid number of entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            name = name.TrimEnd('/');
            if (name.Length == 0 || name.Length > 240 || name.StartsWith('/') ||
                name.Split('/').Any(p => p is "." or ".." || p.Length == 0 || p.EndsWith('.') || p.EndsWith(' ') ||
                    p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                    Regex.IsMatch(p, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("The ZIP contains an unsafe path.");
            if (!paths.Add(name)) throw new InvalidDataException("The ZIP contains duplicate paths.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links and special files are not permitted in update archives.");
            if (entry.Length < 0 || entry.Length > 256L * 1024 * 1024 || entry.Length > expandedLimit - total)
                throw new InvalidDataException("The ZIP exceeds the expanded size limit.");
            total += entry.Length;
            var path = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            UpdateFiles.RequireChild(destination, path);
            UpdateFiles.RequireNoLinks(path);
            if (directory)
            {
                if (entry.Length != 0 || File.Exists(path)) throw new InvalidDataException("Invalid ZIP directory entry.");
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyBoundedAsync(input, output, entry.Length, ct);
            if (output.Length != entry.Length) throw new InvalidDataException("A ZIP entry does not match its declared size.");
        }
    }

    private static string FindPackageRoot(string extracted, string required)
    {
        if (File.Exists(Path.Combine(extracted, required))) return extracted;
        var children = Directory.GetDirectories(extracted);
        if (children.Length == 1 && Directory.GetFiles(extracted).Length == 0 && File.Exists(Path.Combine(children[0], required))) return children[0];
        throw new InvalidDataException("The update ZIP does not have a supported package layout.");
    }

    private static async Task ValidateDistributionAsync(string root, CancellationToken ct)
    {
        UpdateFiles.RequireNoLinks(root);
        if (!Directory.Exists(root)) throw new InvalidDataException("The package does not contain a zapret distribution.");
        var candidate = new ZapretDistribution(root);
        var missing = candidate.Validate();
        if (missing.Count > 0) throw new InvalidDataException("Incomplete zapret package: " + string.Join(", ", missing));
        foreach (var binary in new[] { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys" })
            UpdateFiles.RequirePe(Path.Combine(root, binary));
        var strategies = new BatStrategyProvider(candidate);
        var parsed = await strategies.GetStrategiesAsync(ct);
        if (parsed.Count == 0 || strategies.Errors.Count > 0)
            throw new InvalidDataException("The package contains missing or unsupported strategies: " + string.Join("; ", strategies.Errors));
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (read > limit - copied) throw new InvalidDataException("The download or extracted entry exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
        }
    }

    private static string Decode(byte[] data) => new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF');
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

internal static class UpdateFiles
{
    internal static void RequireChild(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update path is outside its expected directory.");
    }

    internal static void RequireDistinctTrees(string first, string second)
    {
        var left = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var right = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) || right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installed files and prepared updates must be in separate directory trees.");
        if (Path.GetPathRoot(first)!.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A drive root cannot be updated.");
    }

    internal static void RequireNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not allowed in update paths.");
    }

    internal static void RequireFile(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        RequireNoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("Required package file is missing: " + relative);
    }

    internal static void RequirePe(string path)
    {
        RequireNoLinks(path);
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input);
        if (input.Length < 70 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Invalid Windows executable: " + Path.GetFileName(path));
        input.Position = 60;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > input.Length - 6) throw new InvalidDataException("Invalid PE header.");
        input.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw new InvalidDataException("Only x64 PE binaries can be installed.");
    }

    internal static IReadOnlyDictionary<string, string> HashTree(string root, CancellationToken ct)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            hashes.Add(Path.GetRelativePath(root, file), HashFileOrMissing(file));
        }
        return hashes;
    }

    internal static void VerifyTree(string root, IReadOnlyDictionary<string, string> hashes, CancellationToken ct)
    {
        var actual = HashTree(root, ct);
        if (actual.Count != hashes.Count || hashes.Any(x => !actual.TryGetValue(x.Key, out var hash) || hash != x.Value))
            throw new InvalidDataException("Prepared update files changed after validation. Download the update again.");
    }

    internal static string HashFileOrMissing(string path)
    {
        RequireNoLinks(path);
        if (!File.Exists(path)) return "missing";
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    internal static IEnumerable<string> EnumerateFiles(string root)
    {
        RequireNoLinks(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            RequireNoLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RequireNoLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    internal static void CopyTree(string source, string destination, CancellationToken ct)
    {
        RequireNoLinks(destination);
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            new DirectoryInfo(destination).Create(DesktopUpdateHelper.InstallationSecurity());
        else Directory.CreateDirectory(destination);
        foreach (var file in EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            RequireChild(destination, target);
            RequireNoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    internal static void PreserveUserFiles(string current, string replacement, CancellationToken ct, bool preserveUnknown = true)
    {
        if (!Directory.Exists(current)) return;
        foreach (var file in EnumerateFiles(current))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(current, file);
            var target = Path.Combine(replacement, relative);
            var name = Path.GetFileName(file);
            var normalized = relative.Replace('\\', '/');
            var editableList = normalized.StartsWith("lists/", StringComparison.OrdinalIgnoreCase) &&
                normalized.Count(character => character == '/') == 1 && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            var userFile = editableList || name.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".backup", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ipset-all.txt", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) ||
                name.Equals("settings.json", StringComparison.OrdinalIgnoreCase);
            if (!userFile && (!preserveUnknown || File.Exists(target))) continue;
            RequireChild(replacement, target);
            RequireNoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    internal static void SwapDirectories(string target, string replacement, string backup)
    {
        RequireNoLinks(target);
        RequireNoLinks(replacement);
        RequireNoLinks(backup);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Installed directory is missing.");
        Directory.Move(target, backup);
        try { Directory.Move(replacement, target); }
        catch
        {
            Directory.Move(backup, target);
            throw;
        }
    }

    internal static async Task<string?> ReplaceTextAsync(string target, string content, CancellationToken ct)
    {
        RequireNoLinks(target);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".zapret-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = File.Exists(target) ? target + ".zapret-backup-" + Guid.NewGuid().ToString("N") : null;
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            RequireNoLinks(target);
            if (backup is not null) File.Replace(temp, target, backup);
            else File.Move(temp, target);
            return backup;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void DeleteTree(string root)
    {
        // Check every entry before recursive deletion so a linked entry is never followed.
        _ = EnumerateFiles(root).ToArray();
        Directory.Delete(root, recursive: true);
    }
}
