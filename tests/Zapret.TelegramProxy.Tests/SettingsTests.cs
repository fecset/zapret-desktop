using System.Text.Json;
using Zapret.Core;
using Zapret.Infrastructure;
namespace Zapret.TelegramProxy.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task FirstRunDoesNotCreateSecretAndStorageRoundTripsWithoutPlaintext()
    {
        var directory = Directory.CreateTempSubdirectory("Telegram-proxy-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "telegram-proxy.json"); var store = new TelegramProxySettingsStore(path);
            Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken)); Assert.False(File.Exists(path));
            var options = new TelegramProxyOptions { Secret = TelegramProxyOptions.GenerateSecret() };
            await store.SaveAsync(options, TestContext.Current.CancellationToken); var stored = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(options.Secret, stored); Assert.DoesNotContain("dd" + options.Secret, stored);
            Assert.Equal(options.Secret, (await store.LoadAsync(TestContext.Current.CancellationToken))!.Secret); Assert.False((await store.LoadAsync(TestContext.Current.CancellationToken))!.AutoStart);
            Assert.Equal($"tg://proxy?server=127.0.0.1&port=1443&secret=dd{options.Secret}", options.ConnectionLink());
            Assert.DoesNotContain(options.Secret, options.ToString());
        }
        finally { directory.Delete(true); }
    }
    [Theory]
    [InlineData("{")] [InlineData("null")] [InlineData("{\"Version\":99}")]
    [InlineData("{\"Version\":1,\"Options\":{},\"ProtectedSecret\":\"broken\"}")]
    public async Task CorruptSettingsAreReportedAndNeverOverwritten(string content)
    {
        var directory = Directory.CreateTempSubdirectory("Telegram-proxy-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "telegram-proxy.json"); await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramProxySettingsStore(path).LoadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(content, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { directory.Delete(true); }
    }
    [Theory]
    [InlineData(0)] [InlineData(65536)] [InlineData(-1)]
    public void InvalidPortRejected(int port) => Assert.Throws<ArgumentException>(() => new TelegramProxyOptions { Port = port, Secret = new string('0',32) }.Validate());
    [Theory]
    [InlineData("https://example.com")] [InlineData("a.example/x")] [InlineData("a.example:443")] [InlineData("127.0.0.1")] [InlineData("a\r\nb.example")]
    public void InvalidDomainsRejected(string domain) => Assert.Throws<ArgumentException>(() => new TelegramProxyOptions { Secret = new string('0',32), WorkerDomains = [domain] }.Validate());
    [Fact]
    public void DcOverridesAndResourceLimitsAreValidated()
    {
        var options = new TelegramProxyOptions { Secret = new string('0',32) };
        Assert.Throws<ArgumentException>(() => (options with { DcOverrides = new() { [6] = "1.1.1.1" } }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { DcOverrides = new() { [2] = "127.0.0.1" } }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { MaxConnections = 10000 }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { Secret = new string('z',32) }).ConnectionLink());
        Assert.NotEqual(TelegramProxyOptions.GenerateSecret(), TelegramProxyOptions.GenerateSecret());
    }
}
