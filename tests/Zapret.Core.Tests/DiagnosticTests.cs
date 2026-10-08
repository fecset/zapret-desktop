using Zapret.Core;
using Zapret.Infrastructure;
using System.Net;

namespace Zapret.Core.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public async Task RuntimeFailureIsReportedWithoutDiscardingTheOtherDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZapretDiagnostics-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new DiagnosticService(new ZapretDistribution(root), new EmptyStrategies(),
                new Privilege(), new FailedProcess(), new FakeSystem());
            var result = await service.RunAsync();
            Assert.Contains(result, item => item.Name == "Состояние Zapret" && !item.Passed && item.Detail.Contains("runtime unavailable"));
            Assert.Contains(result, item => item.Name == "bin/winws.exe" && !item.Passed);
            Assert.Contains(result, item => item.Name == "DNS discord.com" && item.Passed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ChecksUseDnsHostsAndBothProxySourcesAndReportEachFailureSeparately()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZapretDiagnostics-tests-" + Guid.NewGuid().ToString("N"));
        var system = new FakeSystem
        {
            Hosts = "# 0.0.0.0 youtube.com\n127.0.0.1 unrelated.test\n0.0.0.0 WWW.YouTube.com discord.com # custom aliases",
            Proxy = new(true, "https://user:secret@proxy.test:8080", "https://pac.test/config", new Dictionary<string, string?> { ["HTTPS_PROXY"] = "http://private:password@localhost:1234" }),
            ProcessNames = ["AdguardSvc", "winws", "winws"],
            FailDiscordDns = true,
            FailBfe = true
        };
        var service = new DiagnosticService(new ZapretDistribution(root), new EmptyStrategies(),
            new Privilege(), new ActiveProcess(), system);
        var result = await service.RunAsync();
        Assert.Contains(result, item => item.Name == "Комплект дистрибутива" && !item.Passed);
        Assert.Contains(result, item => item.Name == "DNS www.youtube.com" && item.Passed && item.Detail.Contains("203.0.113.5"));
        Assert.Contains(result, item => item.Name == "DNS discord.com" && !item.Passed);
        Assert.Contains(result, item => item.Name == "Hosts" && !item.Passed && item.Detail.Contains("WWW.YouTube.com"));
        Assert.Contains(result, item => item.Name == "Прокси Windows (WinINET)" && !item.Passed);
        Assert.Contains(result, item => item.Name == "Прокси окружения" && !item.Passed && item.Detail.Contains("HTTPS_PROXY"));
        Assert.Contains(result, item => item.Name == "Возможные конфликты ПО" && !item.Passed && item.Detail.Contains("AdguardSvc"));
        Assert.Contains(result, item => item.Name == "Системная служба фильтрации Windows (BFE)" && !item.Passed);
        Assert.DoesNotContain(result, item => item.Detail.Contains("secret") || item.Detail.Contains("password"));
    }

    [Fact]
    public async Task ActiveParametersAreReadFromTheRunningProcessInsteadOfTheSelectedStrategy()
    {
        var system = new FakeSystem { CommandLine = "winws.exe --wf-tcp=443 --dpi-desync=fake" };
        var service = new DiagnosticService(new ZapretDistribution(Path.GetTempPath()), new EmptyStrategies(),
            new Privilege(), new ActiveProcess(), system);
        var result = await service.RunAsync();
        var parameters = Assert.Single(result, item => item.Name == "Фактические параметры winws");
        Assert.True(parameters.Passed);
        Assert.Contains("--dpi-desync=fake", parameters.Detail);
        Assert.Contains("42", parameters.Detail);
    }

    [Fact]
    public async Task CancelledDiagnosticsPropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new DiagnosticService(new ZapretDistribution(Path.GetTempPath()), new EmptyStrategies(),
            new Privilege(), new ActiveProcess(), new FakeSystem());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(cancellation.Token));
    }

    [Fact]
    public async Task HostsCommentsAndLookalikeDomainsDoNotCountAsServiceOverrides()
    {
        var system = new FakeSystem
        {
            Hosts = "# 0.0.0.0 discord.com\n127.0.0.1 discord.com.example.test fake-youtube.com\n127.0.0.1 local.test # youtube.com"
        };
        var service = new DiagnosticService(new ZapretDistribution(Path.GetTempPath()), new EmptyStrategies(),
            new Privilege(), new ActiveProcess(), system);
        var result = await service.RunAsync();
        Assert.True(Assert.Single(result, item => item.Name == "Hosts").Passed);
    }

    [Fact]
    public async Task LoopbackDnsIsReportedAsAnOverrideInsteadOfHealthyResolution()
    {
        var system = new FakeSystem { DnsAddress = IPAddress.Loopback };
        var service = new DiagnosticService(new ZapretDistribution(Path.GetTempPath()), new EmptyStrategies(),
            new Privilege(), new ActiveProcess(), system);
        var result = await service.RunAsync();
        Assert.False(Assert.Single(result, item => item.Name == "DNS www.youtube.com").Passed);
        Assert.False(Assert.Single(result, item => item.Name == "DNS discord.com").Passed);
    }

    [Fact]
    public async Task CancellationInterruptsAnOngoingDnsCheck()
    {
        var system = new FakeSystem { SlowDns = true };
        var service = new DiagnosticService(new ZapretDistribution(Path.GetTempPath()), new EmptyStrategies(),
            new Privilege(), new ActiveProcess(), system);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(cancellation.Token));
    }

    private sealed class FakeSystem : IDiagnosticSystemProbe
    {
        public string Hosts { get; init; } = "# No overrides";
        public DiagnosticProxyConfiguration Proxy { get; init; } = new(false, null, null, new Dictionary<string, string?>());
        public IReadOnlyList<string> ProcessNames { get; init; } = [];
        public string? CommandLine { get; init; } = "winws.exe --wf-tcp=443";
        public bool FailDiscordDns { get; init; }
        public bool FailBfe { get; init; }
        public bool SlowDns { get; init; }
        public IPAddress DnsAddress { get; init; } = IPAddress.Parse("203.0.113.5");
        public Task<string> GetServiceStatusAsync(string name, CancellationToken cancellationToken) =>
            name == "BFE" && FailBfe ? throw new IOException("BFE unavailable") : Task.FromResult(name == "BFE" ? "Running" : "NotInstalled");
        public Task<IReadOnlyList<string>> GetDnsServersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["Ethernet: 1.1.1.1"]);
        public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (SlowDns) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (host == "discord.com" && FailDiscordDns) throw new System.Net.Sockets.SocketException();
            return [DnsAddress];
        }
        public Task<string> ReadHostsAsync(CancellationToken cancellationToken) => Task.FromResult(Hosts);
        public Task<DiagnosticProxyConfiguration> GetProxyAsync(CancellationToken cancellationToken) => Task.FromResult(Proxy);
        public Task<IReadOnlyDictionary<string, string?>> GetEnvironmentProxyAsync(CancellationToken cancellationToken) => Task.FromResult(Proxy.Environment);
        public Task<IReadOnlyList<string>> GetProcessNamesAsync(CancellationToken cancellationToken) => Task.FromResult(ProcessNames);
        public Task<string?> GetCommandLineAsync(int processId, CancellationToken cancellationToken) => Task.FromResult(CommandLine);
        public Task<string?> GetServiceCommandLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class ActiveProcess : IZapretProcessManager
    {
        public event Action<string, string>? OutputReceived { add { } remove { } }
        public Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ZapretStatus(
            ZapretRunState.Running, 42, DateTimeOffset.UtcNow, "actual.bat", "Running", "NotInstalled"));
        public Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyStrategies : IStrategyProvider
    {
        public IReadOnlyList<string> Errors => [];
        public Task<IReadOnlyList<ZapretStrategy>> GetStrategiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ZapretStrategy>>([]);
    }

    private sealed class Privilege : IPrivilegeService
    {
        public bool IsAdministrator => false;
        public void RestartElevated() => throw new NotSupportedException();
    }

    private sealed class FailedProcess : IZapretProcessManager
    {
        public event Action<string, string>? OutputReceived { add { } remove { } }
        public Task<ZapretStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("runtime unavailable");
        public Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> StartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZapretStatus> RestartAsync(ZapretStrategy strategy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
