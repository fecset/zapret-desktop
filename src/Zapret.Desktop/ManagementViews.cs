using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private void BuildLists()
    {
        Heading("Списки", "Просматривайте и редактируйте домены и IP-адреса в файлах zapret.");
        if (vm.ListFiles.Count == 0)
        {
            page.Children.Add(Panel(Text("Файлы .txt в папке zapret/lists не найдены. Проверьте комплект zapret.", 14, color: WarnBrush)));
            return;
        }
        var tools = new StackPanel { Spacing = 13 };
        tools.Children.Add(Text("Выберите файл", 16, FontWeight.SemiBold));
        var searchRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var files = Dropdown(vm.ListFiles,
            vm.ListFiles.FirstOrDefault(name => name.Equals("list-general.txt", StringComparison.OrdinalIgnoreCase)) ?? vm.ListFiles[0], 235);
        var query = Input("Найти домен или IP-адрес", 245);
        var shown = Text("", 13, color: MutedBrush);
        string SelectedFile() => files.SelectedItem as string ?? vm.ListFiles[0];
        async Task LoadAsync()
        {
            await vm.SearchListAsync(SelectedFile(), query.Text ?? "");
            shown.Text = string.IsNullOrWhiteSpace(query.Text)
                ? vm.ListLines.Count == 500
                    ? $"Показаны первые 500 строк файла {SelectedFile()}. Для остальных строк используйте поиск."
                    : $"Показано строк файла {SelectedFile()}: {vm.ListLines.Count}."
                : $"Найдено совпадений: {vm.ListLines.Count} (не более 500).";
        }
        var find = Button("Найти", LoadAsync, primary: true);
        var open = Button("Открыть папку", () => { vm.OpenListsFolder(); return Task.CompletedTask; });
        foreach (var control in new Control[] { files, query, find, open })
        {
            control.Margin = new Thickness(0, 0, 9, 9);
            searchRow.Children.Add(control);
        }
        tools.Children.Add(searchRow);
        tools.Children.Add(shown);
        page.Children.Add(Panel(tools));

        var edit = new StackPanel { Spacing = 12 };
        var editTitle = Text("Добавить домен", 16, FontWeight.SemiBold);
        edit.Children.Add(editTitle);
        edit.Children.Add(Text("Чтобы удалить запись, выберите строку в списке ниже. Изменения вступят в силу после перезапуска Zapret.", 13, color: MutedBrush));
        var entry = Input("Например, example.com", 280);
        var entries = new ListBox { ItemsSource = vm.ListLines,
            Background = SurfaceBrush, Foreground = TextBrush };
        var editRow = new WrapPanel { Orientation = Orientation.Horizontal };
        entry.Margin = new Thickness(0, 0, 9, 9);
        editRow.Children.Add(entry);
        var add = Button("Добавить домен", async () =>
        {
            await vm.AddListEntryAsync(SelectedFile(), entry.Text ?? "");
            query.Text = "";
            await LoadAsync();
            entry.Text = "";
        }, primary: true);
        var remove = Button("Удалить домен", async () =>
        {
            await vm.RemoveListEntryAsync(SelectedFile(), entry.Text ?? "");
            query.Text = "";
            await LoadAsync();
            entry.Text = "";
        }, danger: true);
        add.Margin = new Thickness(0, 0, 9, 9);
        remove.Margin = new Thickness(0, 0, 9, 9);
        editRow.Children.Add(add);
        editRow.Children.Add(remove);
        edit.Children.Add(editRow);
        page.Children.Add(Panel(edit));
        void UpdateEditor()
        {
            var isIpSet = SelectedFile().StartsWith("ipset-", StringComparison.OrdinalIgnoreCase);
            editTitle.Text = isIpSet ? "Добавить IP-адрес или подсеть" : "Добавить домен";
            entry.PlaceholderText = isIpSet ? "Например, 1.2.3.4/24" : "Например, example.com";
            add.Content = isIpSet ? "Добавить IP" : "Добавить домен";
            remove.Content = "Удалить выбранное";
            add.IsEnabled = remove.IsEnabled = !string.IsNullOrWhiteSpace(entry.Text);
        }
        files.SelectionChanged += async (_, _) =>
        {
            query.Text = "";
            entry.Text = "";
            UpdateEditor();
            await LoadAsync();
        };
        entry.TextChanged += (_, _) => UpdateEditor();
        entries.SelectionChanged += (_, _) =>
        {
            if (entries.SelectedItem is string selected) entry.Text = selected.Trim();
        };
        UpdateEditor();
        FillRemainingHeight(Panel(entries, 8));
        _ = LoadAsync();
    }

    private void BuildDiagnostics()
    {
        Heading("Диагностика", "Проверка компонентов и подсказки для устранения неполадок.");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        actions.Children.Add(Button("Проверить систему", async () => { await vm.DiagnoseAsync(); Show("Диагностика"); }, primary: true));
        actions.Children.Add(Button("Скопировать отчёт", async () =>
        {
            if (Clipboard is not null) await Clipboard.SetTextAsync(vm.BuildDiagnosticReport());
        }));
        page.Children.Add(actions);
        if (vm.Diagnostics.Count == 0)
        {
            page.Children.Add(Panel(Text("Нажмите «Проверить систему», чтобы увидеть состояние файлов, драйвера и службы.", 13, color: MutedBrush)));
            return;
        }
        var good = vm.Diagnostics.Count(x => x.Passed);
        page.Children.Add(Pill($"{good} ИЗ {vm.Diagnostics.Count} ПРОВЕРОК ПРОЙДЕНО", good == vm.Diagnostics.Count ? GoodBrush : WarnBrush));
        foreach (var item in vm.Diagnostics)
        {
            var row = new StackPanel { Spacing = 5 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            title.Children.Add(new PathIcon { Data = item.Passed ? FluentIcons.CheckmarkCircle : FluentIcons.DismissCircle,
                Width = 18, Height = 18, Foreground = item.Passed ? GoodBrush : BadBrush,
                VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(Text(item.Name, 14, FontWeight.SemiBold));
            row.Children.Add(title);
            row.Children.Add(Text(item.Detail, 12, color: MutedBrush));
            if (!item.Passed) row.Children.Add(Text("Что делать: " + item.Solution, 12, color: WarnBrush));
            page.Children.Add(Panel(row, 16));
        }
    }

    private void BuildLogs()
    {
        Heading("Логи", "Новые события отображаются сверху.");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var filter = Dropdown(new[] { "ALL", "INFO", "WARN", "ERROR" }, vm.LogLevelFilter, 125);
        filter.SelectionChanged += (_, _) => vm.LogLevelFilter = filter.SelectedItem as string ?? "ALL";
        tools.Children.Add(filter);
        tools.Children.Add(Button("Копировать", async () =>
        {
            if (Clipboard is not null) await Clipboard.SetTextAsync(string.Join(Environment.NewLine, vm.Logs));
        }));
        tools.Children.Add(Button("Сохранить", async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "zapret-desktop.log" });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await vm.SaveLogsAsync(stream);
        }));
        tools.Children.Add(Button("Очистить", () => { vm.ClearLogs(); return Task.CompletedTask; }, danger: true));
        page.Children.Add(tools);
        var list = new ListBox { ItemsSource = vm.FilteredLogs, Background = SurfaceBrush,
            Foreground = TextBrush, BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas") };
        FillRemainingHeight(Panel(list, 8));
    }

    private void BuildSettings()
    {
        Heading("Настройки", "Запуск, фильтры и параметры приложения.");
        ServiceSettings();
        FilterSettings();
        AppSettings();
        UpdateSettings();
        FakeSettings();
        CreditsSettings();
    }
    private void ServiceSettings()
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Text("Служба Windows", 17, FontWeight.SemiBold));
        stack.Children.Add(Text($"Сейчас: {ServiceLabel(vm.Status?.ServiceStatus)}. Служба позволяет запускать Zapret вместе с Windows.", 12, color: MutedBrush));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var serviceState = vm.Status?.ServiceStatus;
        var available = serviceState == "NotInstalled"
            ? new[] { ("Установить службу", "install") }
            : serviceState == "Running"
                ? new[] { ("Остановить службу", "stop"), ("Перезапустить", "restart"), ("Удалить службу", "remove") }
                : new[] { ("Запустить службу", "start"), ("Удалить службу", "remove") };
        foreach (var (label, operation) in available)
        {
            var button = Button(label, async () => { await vm.ServiceAsync(operation); Show("Настройки"); },
                primary: operation is "install" or "start", danger: operation == "remove");
            button.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(button);
        }
        stack.Children.Add(actions);
        if (serviceState is not null and not "NotInstalled")
        {
            var startup = Check("Запускать службу вместе с Windows", vm.ServiceAutoStart);
            startup.Click += async (_, _) => await vm.ServiceAsync(startup.IsChecked == true ? "auto" : "manual");
            stack.Children.Add(startup);
        }
        if (!vm.IsAdministrator)
            stack.Children.Add(Button("Перезапустить с правами администратора", () => { vm.RequestElevation(); return Task.CompletedTask; }, primary: true));
        page.Children.Add(Panel(stack));
    }
    private void FilterSettings()
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Text("Фильтры", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("После изменения фильтра перезапустите работающую стратегию.", 12, color: WarnBrush));
        stack.Children.Add(Text("Game Filter", 13, FontWeight.SemiBold));
        var game = Dropdown(Enum.GetValues<GameFilterMode>(), vm.Game.Mode, 205);
        game.ItemTemplate = new FuncDataTemplate<GameFilterMode>((mode, _) => Text(mode switch
        {
            GameFilterMode.Disabled => "Выключен", GameFilterMode.All => "TCP и UDP",
            GameFilterMode.Tcp => "Только TCP", GameFilterMode.Udp => "Только UDP", _ => "Неизвестно"
        }, 12));
        var tcp = Input("TCP порты", 170); tcp.Text = vm.Game.TcpPorts;
        var udp = Input("UDP порты", 170); udp.Text = vm.Game.UdpPorts;
        var gameRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in new Control[] { Labelled("Режим", game), Labelled("TCP порты", tcp), Labelled("UDP порты", udp),
            Button("Применить", () => vm.SetGameAsync((GameFilterMode)(game.SelectedItem ?? GameFilterMode.Disabled), tcp.Text ?? "", udp.Text ?? ""), primary: true) })
        { item.Margin = new Thickness(0, 0, 8, 8); gameRow.Children.Add(item); }
        stack.Children.Add(gameRow);
        stack.Children.Add(Text("IPSet Filter", 13, FontWeight.SemiBold));
        var ipset = Dropdown(Enum.GetValues<IpSetMode>(), vm.IpSet, 275);
        ipset.ItemTemplate = new FuncDataTemplate<IpSetMode>((mode, _) => Text(mode switch
        {
            IpSetMode.None => "Не проверять IP-адреса", IpSetMode.Loaded => "Только адреса из списка",
            IpSetMode.Any => "Все IP-адреса", _ => "Неизвестно"
        }, 12));
        var ipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ipRow.Children.Add(ipset);
        ipRow.Children.Add(Button("Применить", () => vm.SetIpSetAsync((IpSetMode)(ipset.SelectedItem ?? IpSetMode.None)), primary: true));
        stack.Children.Add(ipRow);
        page.Children.Add(Panel(stack));
    }
    private void AppSettings()
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Text("Приложение", 17, FontWeight.SemiBold));
        var windows = Check("Запускать приложение вместе с Windows", vm.Settings.StartWithWindows);
        var launch = Check("Запускать Zapret при открытии приложения", vm.Settings.StartZapretOnLaunch);
        var tray = Check("При закрытии скрывать окно в трей", vm.Settings.MinimizeToTray);
        var updates = Check("Проверять обновления при запуске", vm.Settings.CheckUpdates);
        foreach (var check in new[] { windows, launch, tray, updates }) stack.Children.Add(check);
        var themeRow = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        themeRow.Children.Add(Text("Тема", 12, color: MutedBrush));
        var theme = Dropdown(new[] { "System", "Light", "Dark" }, vm.Settings.Theme, 160);
        themeRow.Children.Add(theme);
        stack.Children.Add(themeRow);
        stack.Children.Add(Button("Сохранить настройки", async () =>
        {
            vm.Settings.StartWithWindows = windows.IsChecked == true;
            vm.Settings.StartZapretOnLaunch = launch.IsChecked == true;
            vm.Settings.MinimizeToTray = tray.IsChecked == true;
            vm.Settings.CheckUpdates = updates.IsChecked == true;
            vm.Settings.Theme = theme.SelectedItem as string ?? "System";
            await vm.SaveSettingsAsync();
            ApplyTheme();
        }, primary: true));
        page.Children.Add(Panel(stack));
    }
    private void UpdateSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("Обновления zapret и Zapret Desktop", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("Новые версии проверяются на GitHub. Если доступно обновление, его страница откроется в браузере.", 12, color: MutedBrush));
        updateCheckButton = Button("Проверить обновления", CheckAndOpenUpdatesAsync);
        stack.Children.Add(updateCheckButton);
        upstreamUpdateStatus = Text("", 12);
        desktopUpdateStatus = Text("", 12);
        stack.Children.Add(upstreamUpdateStatus);
        stack.Children.Add(desktopUpdateStatus);
        RefreshUpdateDisplay();
        page.Children.Add(Panel(stack));
    }
    private void FakeSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("Активные fake payloads", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("Выберите файл из комплекта zapret. Изменение начнёт действовать после перезапуска стратегии.", 12, color: MutedBrush));
        var source = Dropdown(vm.FakePayloads, vm.FakePayloads.FirstOrDefault(), 280);
        stack.Children.Add(source);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Button("Применить для Discord", () => vm.ReplaceFakeAsync("ACTIVE_DISCORD_UDP.bin", source.SelectedItem as string ?? "")));
        buttons.Children.Add(Button("Применить для игр", () => vm.ReplaceFakeAsync("ACTIVE_GAME_UDP.bin", source.SelectedItem as string ?? "")));
        stack.Children.Add(buttons);
        page.Children.Add(Panel(stack));
    }
    private void CreditsSettings()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Text("Авторы исходных проектов", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("bol-van — автор zapret: github.com/bol-van/zapret", 14));
        stack.Children.Add(Text("Flowseal — автор Windows-сборки zapret-discord-youtube: github.com/Flowseal/zapret-discord-youtube", 14));
        stack.Children.Add(Text("fecset — автор Zapret Desktop: github.com/fecset/zapret-desktop", 14));
        page.Children.Add(Panel(stack));
    }
    private TextBox Input(string hint, double width) => new()
    {
        PlaceholderText = hint, Width = width, MinHeight = 40, Background = SurfaceRaisedBrush,
        Foreground = TextBrush, BorderBrush = OutlineBrush, CornerRadius = new CornerRadius(8),
        FontSize = 14, VerticalAlignment = VerticalAlignment.Center
    };
    private CheckBox Check(string label, bool value) => new()
    {
        Content = label, IsChecked = value, Foreground = TextBrush, FontSize = 14
    };
    private StackPanel Labelled(string label, Control control)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(Text(label, 11, color: MutedBrush));
        stack.Children.Add(control);
        return stack;
    }
}
