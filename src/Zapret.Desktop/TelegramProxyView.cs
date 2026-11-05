using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Zapret.Core;
namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private Action? refreshTelegramPage;
    private Action? refreshTelegramHome;
    private Action? refreshTelegramSidebar;
    private readonly DispatcherTimer telegramStatsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public void OpenTelegramProxy() { Show("Telegram прокси"); base.Show(); Activate(); }
    private void InitializeTelegramUi()
    {
        if (vm.TelegramProxy is not { } proxy) return;
        void Refresh()
        {
            refreshTelegramPage?.Invoke(); refreshTelegramHome?.Invoke(); refreshTelegramSidebar?.Invoke();
            RefreshButtonActivity();
            if (proxy.CanConnect && !telegramStatsTimer.IsEnabled) telegramStatsTimer.Start();
            else if (!proxy.CanConnect) telegramStatsTimer.Stop();
        }
        proxy.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        telegramStatsTimer.Tick += (_, _) => Refresh();
        Closed += (_, _) => telegramStatsTimer.Stop();
    }
    private IBrush TelegramStatusColor() => vm.TelegramProxy?.Snapshot.State switch
    {
        TelegramProxyState.Running => GoodBrush, TelegramProxyState.Error => BadBrush,
        TelegramProxyState.Starting or TelegramProxyState.Stopping => AccentTextBrush, _ => MutedBrush
    };
    private IBrush TelegramStatusBackground() => Token(vm.TelegramProxy?.Snapshot.State switch
    {
        TelegramProxyState.Running => "goodBg", TelegramProxyState.Error => "badBg",
        TelegramProxyState.Starting or TelegramProxyState.Stopping => "infoBg", _ => "active"
    });
    private Border TelegramStatusPill(out TextBlock label)
    {
        var pill = StatusPill("", TelegramStatusColor(), TelegramStatusBackground());
        label = (TextBlock)((Grid)pill.Child!).Children[1];
        return pill;
    }
    private void UpdateTelegramStatusPill(Border pill, TextBlock label)
    {
        label.Text = vm.TelegramProxy!.StatusText.ToUpperInvariant();
        label.Foreground = ((Border)((Grid)pill.Child!).Children[0]).Background = TelegramStatusColor();
        pill.Background = TelegramStatusBackground();
    }
    private Border TelegramSidebarStatus()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("6,*"), ColumnSpacing = 8 };
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        var text = Text("", 12); text.LineHeight = 18; Grid.SetColumn(text, 1);
        row.Children.Add(dot); row.Children.Add(text);
        var panel = new Border { Child = row, Padding = new Thickness(10, 4), Margin = new Thickness(0, -8, 0, 0) };
        AutomationProperties.SetAutomationId(panel, "TelegramSidebarStatus");
        refreshTelegramSidebar = () =>
        {
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(text, vm.TelegramProxy!.StatusText);
            text.Text = vm.TelegramProxy!.StatusText; text.Foreground = dot.Background = TelegramStatusColor();
            panel.Background = Token(vm.TelegramProxy.CanConnect ? "goodBg" : "active");
        };
        refreshTelegramSidebar(); return panel;
    }
    private Button TelegramToggleButton()
    {
        var proxy = vm.TelegramProxy!;
        var button = Button("Запустить прокси", async () => { if (proxy.CanStop) await proxy.StopAsync(); else await proxy.StartAsync(); },
            primary: !proxy.CanConnect, danger: proxy.CanConnect, activityKey: "telegram-toggle", isLoading: () => proxy.ChangingRunState);
        button.Width = EngineeringTheme.ActionWidth;
        RefreshTelegramToggle(button); return button;
    }
    private void RefreshTelegramToggle(Button button)
    {
        var proxy = vm.TelegramProxy!; var stopping = proxy.CanConnect || proxy.Stopping;
        button.Content = proxy.Stopping ? "Останавливается…" : proxy.Starting ? "Запускается…" : stopping ? "Остановить прокси" : "Запустить прокси";
        button.IsEnabled = proxy.CanStart || proxy.CanStop;
        button.Classes.Remove("primary"); button.Classes.Remove("danger");
        button.Classes.Add(stopping ? "danger" : "primary");
        button.Background = stopping ? Token("badBg") : AccentBrush;
        button.Foreground = stopping ? BadBrush : Token("onPrimary");
        button.BorderBrush = stopping ? BadBrush : Token("control");
    }
    private Border TelegramHome()
    {
        var proxy = vm.TelegramProxy!;
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 10 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(new TelegramMark(24) { Foreground = MutedBrush }); heading.Children.Add(CenteredText("Telegram прокси", 16, FontWeight.Medium)); content.Children.Add(heading);
        var details = new StackPanel { Spacing = 4 };
        var pill = TelegramStatusPill(out var status); AutomationProperties.SetAutomationId(pill, "TelegramHomeStatus"); var address = Text("", 12, color: MutedBrush); var traffic = Text("", 12, color: MutedBrush);
        details.Children.Add(pill); details.Children.Add(address); details.Children.Add(traffic);
        details.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(details, 1); content.Children.Add(details);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        var toggle = TelegramToggleButton();
        AutomationProperties.SetAutomationId(toggle, "TelegramHomeToggle");
        var settings = IconButton("Настройки Telegram прокси", FluentIcons.Settings, () => { Show("Telegram прокси"); return Task.CompletedTask; });
        actions.Children.Add(toggle); actions.Children.Add(settings); Grid.SetRow(actions, 2); content.Children.Add(actions);
        var failure = Text("", 12, color: BadBrush); Grid.SetRow(failure, 3); content.Children.Add(failure);
        AutomationProperties.SetAutomationId(failure, "TelegramHomeError");
        AutomationProperties.SetAutomationId(traffic, "TelegramHomeSummary");
        refreshTelegramHome = () =>
        {
            UpdateTelegramStatusPill(pill, status); address.Text = proxy.Address;
            var state = proxy.Snapshot;
            traffic.Text = $"Соединений: {state.ActiveConnections} · ↑ {Bytes(state.BytesSent)} · ↓ {Bytes(state.BytesReceived)}";
            failure.Text = proxy.Error ?? state.LastError ?? ""; failure.IsVisible = failure.Text.Length > 0;
            RefreshTelegramToggle(toggle);
        };
        refreshTelegramHome();
        var panel = DashboardSection(content, "TelegramHome", new Thickness(EngineeringTheme.SectionPadding));
        return panel;
    }
    private void BuildTelegramProxy()
    {
        Heading("Telegram прокси", "Прокси для Telegram Desktop");
        if (vm.TelegramProxy is not { } proxy) { page.Children.Add(Panel(Text("Telegram прокси недоступен.", 13))); return; }
        var hero = new StackPanel { Spacing = 10 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleRow.Children.Add(new TelegramMark(28) { Foreground = MutedBrush });
        var statePill = TelegramStatusPill(out var title); AutomationProperties.SetAutomationId(title, "TelegramStatus"); titleRow.Children.Add(statePill); hero.Children.Add(titleRow);
        var endpoint = Text("", 13, color: MutedBrush); hero.Children.Add(endpoint);
        var traffic = Text("", 12, color: MutedBrush); hero.Children.Add(traffic);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, IsVisible = false }; hero.Children.Add(progress);
        var toggle = TelegramToggleButton();
        toggle.HorizontalAlignment = HorizontalAlignment.Left; AutomationProperties.SetAutomationId(toggle, "TelegramToggle"); hero.Children.Add(toggle);
        var error = Text("", 13, color: BadBrush); AutomationProperties.SetAutomationId(error, "TelegramError"); hero.Children.Add(error);
        page.Children.Add(Panel(hero));

        var connection = new StackPanel { Spacing = 10 };
        connection.Children.Add(Text("Подключение Telegram", 17, FontWeight.SemiBold));
        var address = Text("", 13); connection.Children.Add(address);
        connection.Children.Add(Text("Ключ", 12, color: MutedBrush));
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,44"), ColumnSpacing = 8, Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var key = Input("Создаётся при запуске", 460); key.Width = double.NaN; key.IsReadOnly = true;
        AutomationProperties.SetName(key, "Ключ подключения Telegram"); AutomationProperties.SetAutomationId(key, "TelegramKey"); keyRow.Children.Add(key);
        var keyNotice = Text("", 12, color: MutedBrush); keyNotice.IsVisible = false;
        var regenerate = IconButton("Обновить ключ", Geometry.Parse("M17.65 6.35A7.95 7.95 0 0 0 12 4a8 8 0 1 0 7.75 10h-2.1a6 6 0 1 1-1.42-6.23L13 11h7V4Z"), async () =>
        {
            await proxy.RegenerateSecretAsync();
            keyNotice.Text = proxy.Error is null ? "Ключ обновлён. Подключите Telegram заново." : "";
            keyNotice.IsVisible = proxy.Error is null;
        }, primary: true);
        ToolTip.SetTip(regenerate, "Обновить ключ и переподключить прокси. Telegram потребуется подключить заново.");
        AutomationProperties.SetHelpText(regenerate, "Создаёт новый ключ. Работающий прокси перезапустится; подключите Telegram заново.");
        AutomationProperties.SetAutomationId(regenerate, "TelegramRegenerateKey"); Grid.SetColumn(regenerate, 1); keyRow.Children.Add(regenerate);
        connection.Children.Add(keyRow); connection.Children.Add(keyNotice);
        var open = Button("Открыть в Telegram", () => { if (proxy.CanConnect) openReleasePage(proxy.ConnectionUri); return Task.CompletedTask; }, primary: true);
        open.HorizontalAlignment = HorizontalAlignment.Left; connection.Children.Add(open); page.Children.Add(Panel(connection));
        var stats = new StackPanel { Spacing = 8 }; stats.Children.Add(Text("Статистика", 17, FontWeight.SemiBold));
        var counters = Text("", 13); AutomationProperties.SetAutomationId(counters, "TelegramStatistics"); stats.Children.Add(counters);
        var lastConnectionIssue = Text("", 12, color: MutedBrush); stats.Children.Add(lastConnectionIssue); page.Children.Add(Panel(stats));

        var settings = new StackPanel { Spacing = 12 };
        var auto = Check("Запускать вместе с Zapret Desktop", proxy.Draft.AutoStart); settings.Children.Add(auto);
        var port = Input("1443", 160); port.HorizontalAlignment = HorizontalAlignment.Left; port.Text = proxy.Draft.Port.ToString(); AutomationProperties.SetName(port, "Локальный порт"); settings.Children.Add(Labelled("Локальный порт", port));
        var verbose = Check("Подробный журнал", proxy.Draft.VerboseLogging); settings.Children.Add(verbose);
        var cf = Check("Резервный маршрут CfProxy", proxy.Draft.CfProxy); settings.Children.Add(cf);
        var h2 = Check("HTTP/2 для фото и видео", proxy.Draft.Http2Media); settings.Children.Add(h2);
        var tcp = Check("Разрешить прямое TCP-подключение", proxy.Draft.DirectTcp); settings.Children.Add(tcp);
        var dc = Input("2:149.154.167.220", 520); dc.HorizontalAlignment = HorizontalAlignment.Left;
        dc.Height = 112; dc.AcceptsReturn = true; dc.TextWrapping = TextWrapping.NoWrap;
        dc.VerticalContentAlignment = VerticalAlignment.Top; dc.Padding = new Thickness(12, 10);
        dc.Text = string.Join(Environment.NewLine, proxy.Draft.DcOverrides.Select(x => $"{x.Key}:{x.Value}"));
        AutomationProperties.SetName(dc, "Датацентры Telegram (DC → IP)"); AutomationProperties.SetAutomationId(dc, "TelegramDcOverrides");
        settings.Children.Add(Labelled("Датацентры Telegram (DC → IP)", dc));
        settings.Children.Add(Text("Одно правило на строку: номер:IP", 12, color: MutedBrush));
        TextBox RoutingInput(string name, string value)
        {
            var input = Input("", 520); input.HorizontalAlignment = HorizontalAlignment.Left; input.Text = value; AutomationProperties.SetName(input, name); settings.Children.Add(Labelled(name, input)); return input;
        }
        var domains = RoutingInput("Домены CfProxy", string.Join(", ", proxy.Draft.CfDomains));
        domains.PlaceholderText = "По умолчанию — список Flowseal";
        var workers = RoutingInput("Домены Worker", string.Join(", ", proxy.Draft.WorkerDomains)); workers.PlaceholderText = "Необязательно";
        var localError = Text("", 12, color: BadBrush); settings.Children.Add(localError);
        var save = Button("Сохранить настройки", async () =>
        {
            try
            {
                if (!int.TryParse(port.Text, out var number)) throw new ArgumentException("Введите целое число для порта.");
                var overrides = new Dictionary<int,string>();
                foreach (var item in (dc.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var pair = item.Split(':'); if (pair.Length != 2 || !int.TryParse(pair[0], out var id)) throw new ArgumentException("Укажите номер датацентра и IPv4: номер:IP.");
                    if (!overrides.TryAdd(id, pair[1].Trim())) throw new ArgumentException("Укажите один адрес для каждого датацентра.");
                }
                await proxy.SaveAsync(proxy.Draft with { Port = number, AutoStart = auto.IsChecked == true, VerboseLogging = verbose.IsChecked == true,
                    CfProxy = cf.IsChecked == true, Http2Media = h2.IsChecked == true, DirectTcp = tcp.IsChecked == true,
                    DcOverrides = overrides, CfDomains = Split(domains.Text), WorkerDomains = Split(workers.Text) });
                localError.Foreground = proxy.Error is null ? MutedBrush : BadBrush;
                localError.Text = proxy.Error ?? (proxy.CanConnect ? "Сохранено. Перезапустите прокси, чтобы применить настройки." : "Настройки сохранены.");
            }
            catch (ArgumentException ex) { localError.Foreground = BadBrush; localError.Text = ex.Message; }
        });
        save.HorizontalAlignment = HorizontalAlignment.Left; settings.Children.Add(save);
        page.Children.Add(Panel(new Expander { Header = "Настройки прокси", Content = settings, HorizontalAlignment = HorizontalAlignment.Stretch }));
        refreshTelegramPage = () =>
        {
            var s = proxy.Snapshot; UpdateTelegramStatusPill(statePill, title);
            endpoint.Text = proxy.Address + (s.StartedAt is { } at ? " · запущен " + at.ToLocalTime().ToString("g") : "");
            traffic.Text = proxy.TrafficStatus; RefreshTelegramToggle(toggle);
            progress.IsVisible = proxy.Busy || s.State is TelegramProxyState.Starting or TelegramProxyState.Stopping;
            error.Text = proxy.Error ?? s.LastError ?? ""; error.IsVisible = error.Text.Length > 0;
            address.Text = "Сервер: 127.0.0.1 · порт: " + (proxy.CanConnect ? s.Port : proxy.Draft.Port);
            key.Text = proxy.DisplayKey; open.IsEnabled = proxy.CanConnect;
            counters.Text = $"Соединения: {s.ActiveConnections} · всего: {s.TotalConnections}\nПередано: {Bytes(s.BytesSent)} · получено: {Bytes(s.BytesReceived)}\nМаршрут: {(string.IsNullOrEmpty(s.Routes) ? "—" : s.Routes)}";
            counters.Text += $"\nСбои соединений: {s.ConnectionErrors} · отклонено клиентов: {s.RejectedConnections}";
            lastConnectionIssue.IsVisible = s.LastConnectionError is not null;
            lastConnectionIssue.Text = s.LastConnectionError is null ? "" : $"Последняя проблема соединения ({s.LastConnectionErrorAt?.ToLocalTime():HH:mm:ss}):\n{s.LastConnectionError}";
            save.IsEnabled = regenerate.IsEnabled = !proxy.Busy && (proxy.CanStart || proxy.CanStop);
        };
        refreshTelegramPage();
    }
    private static string[] Split(string? value) => (value ?? "").Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string Bytes(long value) => value < 1024 ? $"{value} Б" : value < 1048576 ? $"{value / 1024d:F1} КБ" : $"{value / 1048576d:F1} МБ";
}
