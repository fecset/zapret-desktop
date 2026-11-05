using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
namespace Zapret.Desktop;

public sealed class TelegramProxyTrayMenu : IDisposable
{
    private readonly TelegramProxyViewModel vm;
    public NativeMenu Menu { get; } = new();
    public NativeMenuItem Status { get; } = new() { IsEnabled = false };
    public NativeMenuItem Toggle { get; } = new();
    public NativeMenuItem Connect { get; } = new() { Header = "Открыть в Telegram" };
    public TelegramProxyTrayMenu(TelegramProxyViewModel vm, Action settings, Action<Uri> open)
    {
        this.vm = vm; Menu.Items.Add(Status); Menu.Items.Add(Toggle); Menu.Items.Add(Connect);
        var options = new NativeMenuItem { Header = "Настройки Telegram прокси" }; options.Click += (_, _) => settings(); Menu.Items.Add(options);
        Toggle.Click += async (_, _) => { if (vm.CanStop) await vm.StopAsync(); else await vm.StartAsync(); };
        Connect.Click += (_, _) => { if (vm.CanConnect) open(vm.ConnectionUri); };
        vm.PropertyChanged += Changed; Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Refresh);
    public void Refresh()
    { Status.Header = vm.StatusText; Toggle.Header = vm.CanConnect ? "Остановить" : "Запустить"; Toggle.IsEnabled = vm.CanStart || vm.CanStop; Connect.IsEnabled = vm.CanConnect; }
    public void Dispose() => vm.PropertyChanged -= Changed;
}
