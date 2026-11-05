using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Zapret.Core;
namespace Zapret.Desktop;

public sealed class TelegramProxyViewModel : ObservableObject
{
    private enum Operation { None, Initializing, Starting, Stopping, Saving, Regenerating }
    private readonly ITelegramProxy server;
    private readonly ITelegramProxySettingsStore store;
    private readonly SemaphoreSlim gate = new(1);
    private TelegramProxyOptions saved = new(), draft = new();
    private TelegramProxyOptions? running;
    private bool busy, loaded;
    private string? error, lastLog;
    private Operation operation;
    private Task? initialization;
    public TelegramProxyViewModel(ITelegramProxy server, ITelegramProxySettingsStore store)
    {
        this.server = server; this.store = store;
        server.Changed += () => Dispatcher.UIThread.Post(Refresh);
        server.Diagnostic += entry => Dispatcher.UIThread.Post(() => LogReceived?.Invoke(entry.Level, entry.Message));
    }
    public event Action<string, string>? LogReceived;
    public event Action<string, bool>? FeedbackReceived;
    public TelegramProxySnapshot Snapshot => server.Snapshot;
    public TelegramProxyOptions Draft { get => draft; private set { draft = value; OnPropertyChanged(); } }
    public bool Busy { get => busy; private set { SetProperty(ref busy, value); Refresh(); } }
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public bool CanConnect => Snapshot.State == TelegramProxyState.Running && running is not null;
    public bool CanStart => loaded && !Busy && Snapshot.State is TelegramProxyState.Stopped or TelegramProxyState.Error;
    public bool CanStop => !Busy && Snapshot.State == TelegramProxyState.Running;
    public bool Starting => operation == Operation.Starting || Snapshot.State == TelegramProxyState.Starting;
    public bool Stopping => operation == Operation.Stopping || Snapshot.State == TelegramProxyState.Stopping;
    public bool ChangingRunState => Starting || Stopping || operation == Operation.Regenerating && running is not null;
    public string StatusText => Starting ? "TG Прокси запускается" : Stopping ? "TG Прокси останавливается" : Snapshot.State switch
    {
        TelegramProxyState.Running => "TG Прокси активен",
        TelegramProxyState.Error => "Ошибка TG Прокси", _ => "TG Прокси выключен"
    };
    public string Address => $"127.0.0.1:{(CanConnect ? running!.Port : Draft.Port)}";
    public string ConnectionSecret => CanConnect ? "dd" + running!.Secret : "";
    public string DisplayKey => CanConnect ? ConnectionSecret : Draft.Secret.Length == 0 ? "" : "dd" + Draft.Secret;
    public Uri ConnectionUri => CanConnect ? new(running!.ConnectionLink()) : throw new InvalidOperationException("Сначала запустите прокси.");
    public string ConnectionSettings => CanConnect ? $"MTProto\nСервер: 127.0.0.1\nПорт: {running!.Port}\nКлюч: dd{running.Secret}".Replace("\n", Environment.NewLine) : "";
    public string TrafficStatus => Starting ? "Запуск локального сервера…" : Stopping ? "Закрытие соединений…" : !CanConnect ? "Прокси выключен" : Snapshot.ActiveConnections == 0 ? "Ожидание Telegram" :
        Snapshot.BytesSent > 0 && Snapshot.BytesReceived > 0 ? "Есть обмен данными" : "Клиент подключён";
    public void Refresh()
    {
        var s = Snapshot;
        var line = "Telegram прокси: " + StatusText + (s.LastError is null ? "" : "; " + s.LastError) +
            (saved.VerboseLogging ? $"; клиенты {s.ActiveConnections}/{s.TotalConnections}; ↑{s.BytesSent} ↓{s.BytesReceived}; маршрут {s.Routes}" : "");
        if (line != lastLog) { lastLog = line; LogReceived?.Invoke(s.State == TelegramProxyState.Error ? "ERROR" : "INFO", line); }
        foreach (var property in new[] { nameof(Snapshot), nameof(StatusText), nameof(CanStart), nameof(CanStop), nameof(CanConnect),
            nameof(Address), nameof(TrafficStatus), nameof(Starting), nameof(Stopping), nameof(ChangingRunState) }) OnPropertyChanged(property);
    }
    public Task InitializeAsync() => initialization ??= OperateAsync(Operation.Initializing, async () =>
    {
        var value = await store.LoadAsync(); saved = value ?? new(); Draft = saved.Copy(); loaded = true;
        if (saved.AutoStart)
        {
            operation = Operation.Starting; Refresh(); FeedbackReceived?.Invoke("Запускаем Telegram прокси…", false);
            await StartCoreAsync(); FeedbackReceived?.Invoke("Telegram прокси запущен — " + Address, false);
        }
    });
    private async Task OperateAsync(Operation requested, Func<Task> action, Func<string>? success = null, string? progress = null)
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            operation = requested; Busy = true; Error = null;
            if (progress is not null) FeedbackReceived?.Invoke(progress, false);
            await action();
            if (success is not null) FeedbackReceived?.Invoke(success(), false);
        }
        catch (Exception ex)
        {
            Error = ex is InvalidDataException or ArgumentException or IOException ? ex.Message : server.Snapshot.LastError ?? "Telegram прокси: " + ex.GetType().Name;
            FeedbackReceived?.Invoke(Error, true);
        }
        finally { operation = Operation.None; Busy = false; gate.Release(); }
    }
    public Task StartAsync() => OperateAsync(Operation.Starting, StartCoreAsync,
        () => "Telegram прокси запущен — " + Address, "Запускаем Telegram прокси…");
    private async Task StartCoreAsync()
    {
        if (!loaded) throw new InvalidOperationException("Настройки прокси не загружены.");
        if (server.Snapshot.State == TelegramProxyState.Running) return;
        var options = Draft.Copy();
        if (options.Secret.Length == 0) options = options with { Secret = TelegramProxyOptions.GenerateSecret() };
        options.Validate(); await store.SaveAsync(options); saved = options.Copy(); Draft = options.Copy();
        await server.StartAsync(options); running = options; Refresh();
    }
    public Task StopAsync() => OperateAsync(Operation.Stopping, async () => { await server.StopAsync(); running = null; Refresh(); },
        () => "Telegram прокси остановлен", "Останавливаем Telegram прокси…");
    public Task SaveAsync(TelegramProxyOptions options) => OperateAsync(Operation.Saving, async () =>
    {
        if (!loaded) throw new InvalidOperationException("Настройки прокси не загружены.");
        if (options.Secret.Length == 0) options = options with { Secret = TelegramProxyOptions.GenerateSecret() };
        options.Validate(); await store.SaveAsync(options); saved = options.Copy(); Draft = options.Copy();
    }, () => CanConnect ? "Настройки Telegram прокси сохранены. Для применения перезапустите прокси." : "Настройки Telegram прокси сохранены");
    public Task RegenerateSecretAsync() => OperateAsync(Operation.Regenerating, async () =>
    {
        if (!loaded) throw new InvalidOperationException("Настройки прокси не загружены.");
        var key = TelegramProxyOptions.GenerateSecret();
        var options = saved with { Secret = key };
        var active = CanConnect ? running! with { Secret = key } : null;
        await store.SaveAsync(options); saved = options; Draft = options.Copy();
        if (active is not null) { await server.RestartAsync(active); running = active; }
        Refresh();
    }, () => "Ключ Telegram прокси обновлён. Подключите Telegram заново.", "Обновляем ключ Telegram прокси…");
    public async Task ShutdownAsync() { await gate.WaitAsync(); try { await server.StopAsync(); running = null; Refresh(); } finally { gate.Release(); } }
}
