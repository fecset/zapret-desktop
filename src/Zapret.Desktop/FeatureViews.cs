using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Zapret.Core;

namespace Zapret.Desktop;

public sealed partial class MainWindow
{
    private void ConnectionPanel()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Text("Доступность сервисов", 16, FontWeight.SemiBold));
        stack.Children.Add(Text("Проверка HTTP/TLS отдельно от состояния движка. Она не подтверждает голосовую связь Discord.", 12, color: MutedBrush));
        stack.Children.Add(Button("Проверить соединение", vm.CheckConnectionAsync));
        if (vm.ConnectionHealth is { } health)
        {
            stack.Children.Add(Text("Последняя проверка: " + health.CheckedAt.ToLocalTime().ToString("g"), 11, color: MutedBrush));
            foreach (var target in health.Targets)
                stack.Children.Add(Text($"{target.Name}: {(target.Passed ? "доступен" : "ошибка")} · {target.Duration.TotalMilliseconds:F0} мс · {target.Detail}",
                    12, color: target.Passed ? GoodBrush : WarnBrush));
        }
        else stack.Children.Add(Text("Соединение ещё не проверялось", 12, color: MutedBrush));
        page.Children.Add(Panel(stack, 16));
    }

    private void BackupSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("Резервные копии и перенос данных", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("Настройки, списки, фильтры и payload. Перед импортом и откатом создаётся копия текущих данных. Остановите Zapret и службу.", 12, color: MutedBrush));
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        AddFeatureAction(row, "Создать копию", vm.CreateBackupAsync);
        AddFeatureAction(row, "Экспорт ZIP", async () =>
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    SuggestedFileName = "zapret-user-data.zip", DefaultExtension = "zip"
                });
                if (file is null) return;
                await using var stream = await file.OpenWriteAsync();
                await vm.ExportUserDataAsync(stream);
            }
            catch (Exception ex) { vm.ReportError("Не удалось экспортировать данные", ex); }
        });
        AddFeatureAction(row, "Импорт ZIP", async () =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    AllowMultiple = false, Title = "Импорт пользовательских данных",
                    FileTypeFilter = [new FilePickerFileType("ZIP архив") { Patterns = ["*.zip"] }]
                });
                if (files.Count == 0) return;
                await using var stream = await files[0].OpenReadAsync();
                await vm.ImportUserDataAsync(stream);
                ApplyTheme();
            }
            catch (Exception ex) { vm.ReportError("Не удалось импортировать данные", ex); }
        });
        stack.Children.Add(row);
        var chooser = Dropdown(vm.Backups, vm.Backups.FirstOrDefault(), 570);
        chooser.ItemTemplate = new FuncDataTemplate<UserDataBackupInfo>((backup, _) =>
            Text(backup is null ? "Резервных копий пока нет" : $"{backup.CreatedAt.ToLocalTime():g} · {backup.Reason} · {backup.SizeBytes / 1024:N0} КБ", 12));
        stack.Children.Add(chooser);
        stack.Children.Add(Button("Восстановить выбранную копию", async () =>
        {
            if (chooser.SelectedItem is UserDataBackupInfo backup)
            {
                await vm.RestoreBackupAsync(backup.Id);
                ApplyTheme();
                if (currentPage == "Настройки") Show("Настройки");
            }
        }));
        if (vm.Backups.Count == 0) stack.Children.Add(Text("Резервных копий пока нет", 12, color: MutedBrush));
        page.Children.Add(Panel(stack));
    }

    private void ManagedUpdateSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("Установка обновлений", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("Сначала загрузите и проверьте обновление, затем установите его. Пользовательские данные сохраняются; предыдущий zapret доступен для отката.", 12, color: MutedBrush));
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        AddFeatureAction(row, "Загрузить zapret", vm.PrepareDistributionUpdateAsync);
        if (vm.DistributionUpdatePlan is { } distributionPlan)
        {
            stack.Children.Add(Text(distributionPlan.Summary + $" · {distributionPlan.FileCount} файлов · {distributionPlan.Size / 1024 / 1024:N1} МБ", 12));
            AddFeatureAction(row, "Установить zapret", vm.ApplyDistributionUpdateAsync);
        }
        if (vm.LastDistributionUpdate is not null) AddFeatureAction(row, "Откатить zapret", vm.RollbackDistributionUpdateAsync);
        AddFeatureAction(row, "Загрузить Desktop", vm.PrepareDesktopUpdateAsync);
        if (vm.DesktopUpdatePlan is { } desktopPlan)
        {
            stack.Children.Add(Text(desktopPlan.Summary, 12));
            AddFeatureAction(row, "Установить Desktop и выйти", async () =>
            {
                if (await vm.InstallDesktopUpdateForExitAsync()) CompleteExit();
            });
        }
        stack.Children.Add(row);
        page.Children.Add(Panel(stack));
    }

    private void NetworkDataSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("Сетевые списки и адреса", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("IPSet — список IP-адресов для фильтрации трафика. Загрузка подготавливает список; установка выполняется кнопкой «Применить IPSet».", 12, color: MutedBrush));
        var data = new WrapPanel { Orientation = Orientation.Horizontal };
        AddFeatureAction(data, "Загрузить IPSet", vm.PrepareIpSetUpdateAsync);
        if (vm.IpSetUpdatePlan is { } ipset)
        {
            stack.Children.Add(Text(ipset.Summary + $" · {ipset.EntryCount} записей", 12));
            AddFeatureAction(data, "Применить IPSet", vm.ApplyIpSetUpdateAsync);
        }
        stack.Children.Add(data);
        stack.Children.Add(Text("Адреса YouTube и Discord (hosts)", 15, FontWeight.SemiBold));
        stack.Children.Add(Text("hosts позволяет задать адреса сайтов вручную, вместо получения через DNS. Здесь можно добавить записи из проекта zapret. Это отдельная настройка Windows, а не исправление всех замечаний диагностики.", 12, color: MutedBrush));
        var hostsActions = new WrapPanel();
        AddFeatureAction(hostsActions, "Показать изменения hosts", vm.PreviewHostsAsync);
        stack.Children.Add(hostsActions);
        if (vm.HostsPreview is { } hosts)
        {
            stack.Children.Add(Text("Предпросмотр — файл ещё не изменён. Сравните вкладки и примените изменения, если хотите использовать эти адреса.", 12, color: MutedBrush));
            stack.Children.Add(Text(hosts.Summary, 12));
            var tabs = new TabControl
            {
                ItemsSource = new[]
                {
                    new TabItem { Header = "Текущий hosts", Content = ReportBox(hosts.CurrentContent) },
                    new TabItem { Header = "Будет после применения", Content = ReportBox(hosts.ProposedContent) }
                }
            };
            stack.Children.Add(tabs);
            stack.Children.Add(Text("Для применения нужны права администратора. Сначала остановите Zapret и службу; текущий файл будет сохранён в резервную копию.", 12, color: MutedBrush));
            stack.Children.Add(Button("Применить изменения hosts", async () =>
            {
                await vm.ApplyHostsAsync();
                if (currentPage == "Настройки") Show("Настройки");
            }, primary: true));
        }
        if (vm.HostsUpdateResult is { } result)
        {
            stack.Children.Add(Text(result.Summary, 12, color: GoodBrush));
            stack.Children.Add(Text("Записи установлены. Перезапустите браузер и проверьте соединение на главной странице. Другие замечания диагностики проверяются отдельно.", 12, color: MutedBrush));
        }
        page.Children.Add(Panel(stack));
    }

    private void PayloadRecoveryControls(StackPanel stack)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (target, label) in new[] { ("ACTIVE_DISCORD_UDP.bin", "Discord"), ("ACTIVE_GAME_UDP.bin", "игр") })
        {
            if (vm.PayloadStates.TryGetValue(target, out var state) && state.CanRestore && !state.IsOriginal)
                AddFeatureAction(row, "Отменить замену для " + label, () => vm.RestorePayloadAsync(target));
        }
        if (row.Children.Count == 0) return;
        stack.Children.Add(Text("Если после замены связь стала хуже, можно вернуть сохранённый исходный файл. Сначала остановите Zapret и службу.", 12, color: MutedBrush));
        stack.Children.Add(row);
    }

    private void StrategyHistoryPanel()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Text("История автоподбора", 16, FontWeight.SemiBold));
        var chooser = Dropdown(vm.TestHistory, vm.TestHistory.FirstOrDefault(), 620);
        chooser.ItemTemplate = new FuncDataTemplate<StrategyHistoryEntry>((entry, _) => Text(
            entry is null ? "История пока пуста" : $"{entry.CreatedAt.ToLocalTime():g} · {(entry.Cancelled ? "прерван" : "завершён")} · {entry.BestStrategy ?? "без рекомендации"}", 12));
        stack.Children.Add(chooser);
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        AddFeatureAction(row, "Открыть результат", async () =>
        {
            if (chooser.SelectedItem is StrategyHistoryEntry entry) await vm.ShowHistoryAsync(entry.Id);
        }, "Стратегии");
        AddFeatureAction(row, "Применить рекомендацию из истории", async () =>
        {
            if (chooser.SelectedItem is StrategyHistoryEntry { BestStrategy: { } recommended })
                await vm.ApplyRecommendationAsync(recommended);
        }, "Стратегии");
        stack.Children.Add(row);
        if (vm.HistoryDetails is { } details)
        {
            stack.Children.Add(Text(details.Entry.ReportStatus, 12, color: MutedBrush));
            var checks = new StackPanel { Spacing = 8 };
            foreach (var check in details.Entry.Checks)
            {
                var state = check.State switch
                {
                    StrategyCheckState.Completed => "завершён", StrategyCheckState.Failed => "ошибка запуска",
                    StrategyCheckState.Cancelled => "прерван", StrategyCheckState.Running => "проверяется", _ => "ожидает проверки"
                };
                checks.Children.Add(Text($"{check.FileName}: {state} · HTTP/TLS: {check.HttpOk} успешно, {check.HttpFailed} ошибок · Ping: {check.PingOk} ответов, {check.PingFailed} без ответа", 11));
            }
            stack.Children.Add(new ScrollViewer { Content = checks, MaxHeight = 240,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            stack.Children.Add(new Expander { Header = "Подробный отчёт", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = ReportBox(details.ReportText) });
            stack.Children.Add(Button("Скрыть результат", () => { vm.CloseHistory(); Show("Стратегии"); return Task.CompletedTask; }));
        }
        page.Children.Add(Panel(stack, 16));
    }

    private TextBox ReportBox(string text) => new()
    {
        Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        Height = 180, Background = SurfaceRaisedBrush, Foreground = TextBrush, FontFamily = new FontFamily("Consolas")
    };
    private void AddFeatureAction(Panel row, string label, Func<Task> action, string section = "Настройки")
    {
        var button = Button(label, async () =>
        {
            await action();
            if (currentPage == section) Show(section);
        });
        button.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(button);
    }
}
