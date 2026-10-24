using System.Net;
using Zapret.Core;

namespace Zapret.Desktop.Tests;

public sealed class PreparationTests
{
    [Fact]
    public async Task ChoosingAnotherStrategyDuringPreparationStartsThatStrategy()
    {
        using var response = new PausedResponse();
        using var client = new HttpClient(response);
        using var app = new TestApplication(updateClient: client);
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(s => s.Id == "general"));
        var prepare = app.ViewModel.PreviewHostsAsync();
        await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            await app.ViewModel.SelectAsync(app.ViewModel.Strategies.Single(s => s.Id == "general (alt)"));
            await app.ViewModel.StartAsync();
            Assert.Equal("general (alt)", app.Process.StartedStrategy?.Id);
            Assert.Equal("general (alt)", (await app.Settings.LoadAsync(TestContext.Current.CancellationToken)).SelectedStrategyId);
        }
        finally { response.Release.TrySetResult(); await prepare; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowPreparationDoesNotPreventStoppingOrExiting(bool exit)
    {
        using var response = new PausedResponse();
        using var client = new HttpClient(response);
        using var app = new TestApplication(updateClient: client);
        await app.ViewModel.InitializeAsync();
        await app.ViewModel.StartAsync();
        var prepare = app.ViewModel.PreviewHostsAsync();
        await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stop = exit ? app.ViewModel.StopOwnedForExitAsync() : StopAsync();
        async Task<bool> StopAsync() { await app.ViewModel.StopAsync(); return true; }
        var completed = await Task.WhenAny(stop, Task.Delay(1500, TestContext.Current.CancellationToken)) == stop;
        response.Release.TrySetResult();
        await prepare;
        await stop;
        Assert.True(completed, "Stopping/exiting waited for an unrelated HTTP preparation.");
        Assert.Equal(ZapretRunState.Stopped, (await app.Process.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        if (exit) Assert.Null(app.ViewModel.HostsPreview);
    }

    private sealed class PausedResponse : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("203.0.113.5 youtube.com\n") };
        }
    }
}
