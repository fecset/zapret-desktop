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
        stack.Children.Add(Text("Проверяем ответы контрольных HTTPS-адресов. Воспроизведение видео YouTube и голосовая связь Discord проверяются отдельно.", 12, color: MutedBrush));
        stack.Children.Add(EngineButton("Проверить соединение", vm.CheckConnectionAsync,
            isLoading: () => vm.CheckingConnection));
        if (vm.ConnectionHealth is { } health)
        {
            stack.Children.Add(Text("Последняя проверка: " + health.CheckedAt.ToLocalTime().ToString("g"), 11, color: MutedBrush));
            foreach (var target in health.Targets)
                stack.Children.Add(Text($"{target.Name}: {(target.Passed ? "HTTPS-запрос успешен" : "ошибка проверки")} · {target.Duration.TotalMilliseconds:F0} мс · {target.Detail}",
                    12, color: target.Passed ? GoodBrush : WarnBrush));
        }
        else stack.Children.Add(Text("Соединение ещё не проверялось", 12, color: MutedBrush));
        page.Children.Add(Panel(stack, 16));
    }

    private void BackupSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text("Резервные копии и перенос данных", 17, FontWeight.SemiBold));
        stack.Children.Add(Text("Копии сохраняют настройки, списки, фильтры и payload. До 10 новых автокопий без дублей; ручные и прежние архивы сохраняются. Обычное сохранение настроек копию не создаёт.", 12, color: MutedBrush));
        stack.Children.Add(Text("Восстановление и импорт новую копию не создают. При необходимости сохраните текущие данные кнопкой «Создать копию». Отсутствующие данные и пустые пользовательские списки в архиве не стирают ваши текущие записи.", 12, color: MutedBrush));
        CheckBox? recoveryConsent = null;
        if (vm.RecoveryRequired)
        {
            stack.Children.Add(Text("IPSet требует восстановления. Выберите исправную копию или импортируйте ZIP и подтвердите восстановление. Новая резервная копия автоматически не создаётся. До завершения запуск Zapret заблокирован.", 13, color: WarnBrush));
            recoveryConsent = Check("Подтверждаю восстановление данных из выбранной копии", false);
            stack.Children.Add(recoveryConsent);
        }
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
            if (vm.RecoveryRequired && recoveryConsent?.IsChecked != true)
            { vm.ReportError("Восстановление не подтверждено", new InvalidOperationException("Отметьте подтверждение восстановления из выбранной копии.")); return; }
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
        chooser.Width = double.NaN;
        chooser.HorizontalAlignment = HorizontalAlignment.Stretch;
        chooser.ItemTemplate = new FuncDataTemplate<UserDataBackupInfo>((backup, _) =>
            Text(backup is null ? "Резервных копий пока нет" : $"{backup.CreatedAt.ToLocalTime():g} · {backup.Reason} · {backup.SizeBytes / 1024d:N1} КБ", 12));
        stack.Children.Add(chooser);
        stack.Children.Add(Button("Восстановить выбранную копию", async () =>
        {
            if (chooser.SelectedItem is UserDataBackupInfo backup)
            {
                if (vm.RecoveryRequired && recoveryConsent?.IsChecked != true)
                { vm.ReportError("Восстановление не подтверждено", new InvalidOperationException("Отметьте подтверждение восстановления из выбранной копии.")); return; }
                await vm.RestoreBackupAsync(backup.Id);
                ApplyTheme();
                if (currentPage == "Настройки") Show("Настройки");
            }
        }));
        if (vm.Backups.Count == 0) stack.Children.Add(Text("Резервных копий пока нет", 12, color: MutedBrush));
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
        stack.Children.Add(Text("hosts — таблица адресов сайтов в Windows. Обычно адреса запрашиваются через DNS; записи hosts задают их напрямую. Здесь можно обновить блок адресов из проекта zapret. Ваши остальные записи сохраняются. Сначала проверьте предложенные изменения.", 12, color: MutedBrush));
        var hostsActions = new WrapPanel();
        AddFeatureAction(hostsActions, "Показать изменения hosts", vm.PreviewHostsAsync);
        stack.Children.Add(hostsActions);
        if (vm.HostsPreview is { } hosts)
        {
            stack.Children.Add(Text(hosts.HasChanges ? "Предпросмотр: файл Windows ещё не изменён." :
                "Изменений нет. Эти адреса уже записаны в hosts; повторная запись и резервная копия не нужны.", 13,
                color: hosts.HasChanges ? TextBrush : GoodBrush));
            if (hosts.HasChanges)
                stack.Children.Add(Text(hosts.AddedCount == 0 && hosts.RemovedCount == 0
                    ? "Адреса те же; изменится текст служебного блока. Остальные строки сохранятся."
                    : $"Сопоставлений добавится: {hosts.AddedCount}. Удалится: {hosts.RemovedCount}. Остальные записи сохранятся.", 13, color: MutedBrush));
            var tabs = new TabControl
            {
                ItemsSource = new[]
                {
                    new TabItem { Header = Text("Сейчас", 13), Content = ReportBox(hosts.CurrentContent) },
                    new TabItem { Header = Text("После изменения", 13), Content = ReportBox(hosts.ProposedContent) }
                }
            };
            stack.Children.Add(new Expander { Header = "Сравнить текст до и после", Content = tabs,
                HorizontalAlignment = HorizontalAlignment.Stretch });
            if (hosts.HasChanges)
                stack.Children.Add(Text("Для применения остановите Zapret и службу. Исходный hosts будет сохранён отдельно перед записью; нужны права администратора.", 12, color: MutedBrush));
            var apply = EngineButton("Применить изменения hosts", async () =>
            {
                await vm.ApplyHostsAsync();
                if (currentPage == "Настройки") Show("Настройки");
            }, () => hosts.HasChanges && !vm.PreparingData && !vm.TestingStrategies, primary: true);
            if (!hosts.HasChanges) ToolTip.SetTip(apply, "Изменений нет — применять нечего");
            stack.Children.Add(apply);
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
        chooser.Width = double.NaN;
        chooser.HorizontalAlignment = HorizontalAlignment.Stretch;
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
        var button = EngineButton(label, async () =>
        {
            await action();
            if (currentPage == section) Show(section);
        }, () => !vm.PreparingData && !vm.TestingStrategies);
        button.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(button);
    }
}
