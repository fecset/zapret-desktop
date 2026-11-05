using Zapret.Core;
namespace Zapret.TelegramProxy.Transport;

// Keep the first occurrence and summarize identical connection failures in a fixed window.
internal sealed class DiagnosticCoalescer : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<TelegramProxyDiagnostic, long> repeats = new();
    private readonly Action<TelegramProxyDiagnostic> publish;
    private readonly ITimer timer;
    private readonly TimeSpan window;
    private bool disposed;
    internal const int MaxDistinct = 64;
    public DiagnosticCoalescer(Action<TelegramProxyDiagnostic> publish, TimeProvider? clock = null, TimeSpan? window = null)
    {
        this.publish = publish; this.window = window ?? TimeSpan.FromSeconds(30);
        timer = (clock ?? TimeProvider.System).CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public void Report(TelegramProxyDiagnostic entry)
    {
        List<TelegramProxyDiagnostic> output = [];
        lock (sync)
        {
            if (disposed) return;
            if (repeats.TryGetValue(entry, out var count)) { repeats[entry] = count + 1; return; }
            if (repeats.Count == MaxDistinct) output.AddRange(Drain());
            if (repeats.Count == 0) timer.Change(window, Timeout.InfiniteTimeSpan);
            repeats.Add(entry, 0); output.Add(entry);
        }
        foreach (var item in output) publish(item);
    }
    public void Flush()
    {
        List<TelegramProxyDiagnostic> output;
        lock (sync)
        {
            if (disposed) return;
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); output = Drain();
        }
        foreach (var item in output) publish(item);
    }
    private List<TelegramProxyDiagnostic> Drain()
    {
        var output = repeats.Where(x => x.Value > 0)
            .Select(x => x.Key with { Message = x.Key.Message + $" (повторений: {x.Value})" }).ToList();
        repeats.Clear(); return output;
    }
    public void Dispose()
    {
        List<TelegramProxyDiagnostic> output;
        lock (sync)
        {
            if (disposed) return;
            disposed = true; output = Drain();
        }
        timer.Dispose();
        foreach (var item in output) publish(item);
    }
}
