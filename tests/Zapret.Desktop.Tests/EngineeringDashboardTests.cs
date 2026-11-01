using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Zapret.Core;
using Zapret.Desktop;

namespace Zapret.Desktop.Tests;

public sealed class EngineeringDashboardTests
{
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task MouseCompletionKeepsPointerFocusAndKeyboardUsesOnlyTheThemeRing(string theme)
    {
        var health = new ControlledHealth();
        using var app = new TestApplication(connection: health);
        var window = await OpenAsync(app, theme, 1200, 800);
        try
        {
            var button = FindById<Button>(window, "CheckConnection");
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center, RawInputModifiers.None);
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
            await WaitAsync(() => app.ViewModel.CheckingConnection);
            health.Complete(Result(true, true));
            await WaitAsync(() => !app.ViewModel.CheckingConnection && !((ActionButton)FindById<Button>(window, "CheckConnection")).IsBusy);
            Pump(window);
            button = FindById<Button>(window, "CheckConnection");
            Assert.Null(button.FocusAdorner);
            var ring = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_FocusRing");
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(ring.BorderBrush).Color.A);
            Capture(window, theme + "-mouse-focus");
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
            Pump(window);
            Assert.Equal(255, Assert.IsAssignableFrom<ISolidColorBrush>(ring.BorderBrush).Color.A);
            Assert.NotEqual(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(ring.BorderBrush).Color);
            Capture(window, theme + "-keyboard-focus");
        }
        finally { health.Complete(Result(true, true)); window.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark", 1.0)]
    [InlineData("Light", 1.0)]
    [InlineData("Dark", 1.5)]
    [InlineData("Light", 2.0)]
    public async Task CaptionGlyphPixelsHaveOneStrokeAndTheSameCenter(string theme, double scale)
    {
        using var app = new TestApplication();
        var window = await OpenAsync(app, theme, 1200, 800);
        try
        {
            window.SetRenderScaling(scale);
            Pump(window);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var encoded = new MemoryStream();
            frame.Save(encoded, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            using var bitmap = SKBitmap.Decode(encoded.ToArray());
            var background = theme == "Light" ? SKColor.Parse("F5F6F7") : SKColor.Parse("101112");
            var foreground = theme == "Light" ? SKColor.Parse("5B626C") : SKColor.Parse("A0A3A8");
            static double Difference(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
            var fullInk = Difference(background, foreground);
            foreach (var (id, length) in new[] { ("Minimize", 9d), ("Maximize", 36d), ("Close", Math.Sqrt(200) * 2) })
            {
                var button = FindById<Button>(window, "WindowCommand:" + id);
                var glyph = button.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "WindowCaptionGlyph");
                var origin = glyph.TranslatePoint(default, window)!.Value;
                double ink = 0, centerX = 0, centerY = 0;
                for (var y = (int)Math.Floor(origin.Y * scale); y < (origin.Y + glyph.Bounds.Height) * scale; y++)
                for (var x = (int)Math.Floor(origin.X * scale); x < (origin.X + glyph.Bounds.Width) * scale; x++)
                {
                    var weight = Difference(bitmap.GetPixel(x, y), background) / fullInk;
                    ink += weight;
                    centerX += weight * ((x + 0.5) / scale - origin.X);
                    centerY += weight * ((y + 0.5) / scale - origin.Y);
                }
                Assert.True(ink > 0);
                Assert.InRange(Math.Abs(centerX / ink - glyph.Bounds.Width / 2), 0, 0.35);
                Assert.InRange(Math.Abs(centerY / ink - glyph.Bounds.Height / 2), 0, 0.35);
                Assert.InRange(ink / (length * scale * scale), 0.8, 1.2);
                Assert.Null(button.FocusAdorner);
            }
            Capture(window, $"{theme}-caption-glyphs-{scale:0.##}");
        }
        finally { window.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task ButtonStatesUseTheApprovedTokensAndKeepKeyboardFocus(string theme)
    {
        var health = new ControlledHealth();
        using var app = new TestApplication(connection: health);
        var window = await OpenAsync(app, theme, 1200, 800);
        try
        {
            var button = FindById<Button>(window, "CheckConnection");
            Assert.Null(button.FocusAdorner);
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center, RawInputModifiers.None);
            Pump(window);
            var surface = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_EngineeringSurface");
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(button.GetVisualDescendants()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter" && p.TemplatedParent == button).Background).Color.A);
            Assert.Equal(theme == "Light" ? "#ffe9ebee" : "#ff1c1e21", surface.Background!.ToString());
            Capture(window, theme + "-1200-hover");
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            Pump(window);
            Assert.Equal(theme == "Light" ? "#ffe0e3e7" : "#ff222428", surface.Background!.ToString());
            Capture(window, theme + "-1200-pressed");
            window.MouseMove(default, RawInputModifiers.None);
            window.MouseUp(default, MouseButton.Left, RawInputModifiers.None);
            Assert.True(button.Focus(NavigationMethod.Tab));
            var check = app.ViewModel.CheckConnectionAsync();
            Pump(window);
            button = FindById<Button>(window, "CheckConnection");
            surface = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_EngineeringSurface");
            Assert.False(button.IsEffectivelyEnabled);
            Assert.Equal(theme == "Light" ? "#ffe2e5e9" : "#ff202226", surface.Background!.ToString());
            Capture(window, theme + "-1200-loading-disabled");
            health.Complete(Result(true, true));
            await check;
            Pump(window);
            Assert.True(FindById<Button>(window, "CheckConnection").IsKeyboardFocusWithin);
            Assert.Equal(0, app.Process.StartCalls);
        }
        finally { health.Complete(Result(false, false)); window.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark", 1440, 900)]
    [InlineData("Light", 1440, 900)]
    [InlineData("Dark", 1200, 800)]
    [InlineData("Light", 1200, 800)]
    public async Task DashboardStatesRenderWithRealControlsAndSafeServices(string theme, int width, int height)
    {
        var health = new ControlledHealth();
        using var app = new TestApplication(connection: health, featureServices: true);
        var window = await OpenAsync(app, theme, width, height);
        try
        {
            Assert.Contains(Texts(window), t => t == "Соединение ещё не проверялось");
            Assert.Equal(2, Texts(window).Count(t => t == "Не проверялось"));
            Assert.True(FindButton(window, "Запустить Zapret").IsEffectivelyEnabled);
            AssertLaunchSectionLayout(window);
            AssertButtonContentsCentered(window);
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            var symbol = Controls(window).Single(c => c.Name == "BrandSymbol");
            var wordmark = Controls(window).Single(c => c.Name == "BrandWordmark");
            var symbolCenter = symbol.TranslatePoint(new Point(symbol.Bounds.Width / 2, symbol.Bounds.Height / 2), window)!.Value;
            var wordmarkCenter = wordmark.TranslatePoint(new Point(wordmark.Bounds.Width / 2, wordmark.Bounds.Height / 2), window)!.Value;
            Assert.InRange(Math.Abs(symbolCenter.Y - wordmarkCenter.Y), 0, 1);
            Assert.InRange(Math.Abs(symbol.Bounds.Height - wordmark.Bounds.Height), 0, 3);
            Capture(window, $"{theme}-{width}-stopped");
            if (theme == "Dark" && width == 1440)
            {
                window.Height = 1120;
                Capture(window, "README-v0.3.0");
                window.Height = height;
                Pump(window);
            }

            app.Process.StatusOverride = new(ZapretRunState.Running, 1234, DateTimeOffset.UtcNow,
                "general", "Running", "NotInstalled");
            await app.ViewModel.RefreshAsync();
            Pump(window);
            Assert.Contains(Texts(window), t => t == "Запущена стратегия: General");
            Assert.DoesNotContain(Controls(window), c => AutomationProperties.GetAutomationId(c) == "RestartNotice");
            AssertMainLayout(window, width, height);
            AssertLaunchSectionLayout(window);
            AssertSectionInsets(window);
            Capture(window, $"{theme}-{width}-never");

            var first = app.ViewModel.CheckConnectionAsync();
            Pump(window);
            Assert.Equal(2, Texts(window).Count(t => t == "Проверяется…"));
            Assert.True(Assert.IsType<ActionButton>(FindButton(window, "Проверить соединение")).IsBusy);
            Assert.True(FindButton(window, "Остановить Zapret").IsEffectivelyEnabled);
            Capture(window, $"{theme}-{width}-checking");
            health.Complete(Result(true, false));
            await first;
            DismissFeedback(window);
            Assert.Contains(Texts(window), t => t == "HTTPS доступен");
            Assert.Contains(Texts(window), t => t == "HTTPS недоступен");
            Capture(window, $"{theme}-{width}-partial");

            Click(FindById<Button>(window, "ConnectionDisclosure:Discord"));
            Pump(window);
            Assert.True(FindById<Border>(window, "ConnectionDetails:Discord").IsVisible);
            Assert.Contains(Texts(window), t => t!.Contains("Тестовая ошибка TLS"));
            Capture(window, $"{theme}-{width}-details-error");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump(window);
            Assert.False(FindById<Border>(window, "ConnectionDetails:Discord").IsVisible);

            health.Reset();
            var retry = app.ViewModel.CheckConnectionAsync();
            Pump(window);
            Capture(window, $"{theme}-{width}-retry");
            health.Complete(Result(true, true));
            await retry;
            DismissFeedback(window);
            Assert.Equal(2, Texts(window).Count(t => t == "HTTPS доступен"));
            AssertMainLayout(window, width, height);
            Capture(window, $"{theme}-{width}-running");
            var homeScroll = FindById<ScrollViewer>(window, "PageScroll");
            homeScroll.Offset = new Vector(0, homeScroll.Extent.Height);
            Pump(window);
            Capture(window, $"{theme}-{width}-running-bottom");
            homeScroll.Offset = default;
            Pump(window);

            var picker = FindById<ComboBox>(window, "LaunchStrategyPicker");
            picker.Focus(NavigationMethod.Tab);
            Pump(window);
            var highlight = picker.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HighlightBackground");
            Assert.Equal(theme == "Light" ? "White" : "#ff131416", highlight.Background!.ToString());
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            window.KeyRelease(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            Pump(window);
            Assert.True(picker.IsDropDownOpen);
            Capture(window, $"{theme}-{width}-strategy-menu");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump(window);
            Assert.False(picker.IsDropDownOpen);
            picker.SelectedItem = app.ViewModel.Strategies.Single(s => s.Id != "general");
            await WaitAsync(() => app.ViewModel.SelectedStrategy?.Id != "general" && !app.ViewModel.Busy);
            DismissFeedback(window);
            Assert.Equal("general", app.ViewModel.Status?.StrategyId);
            Assert.True(FindById<Grid>(window, "RestartNotice").IsVisible);
            Assert.Contains(Texts(window), t => t == "Запущена стратегия: General");
            AssertMainLayout(window, width, height);
            Capture(window, $"{theme}-{width}-pending");

            health.Reset();
            var failed = app.ViewModel.CheckConnectionAsync();
            health.Complete(Result(false, false));
            await failed;
            DismissFeedback(window);
            Assert.Equal(2, Texts(window).Count(t => t == "HTTPS недоступен"));
            Capture(window, $"{theme}-{width}-error");

            foreach (var name in new[] { "Стратегии", "Списки", "Диагностика", "Логи", "Настройки" })
            {
                Click(FindById<Button>(window, "Navigation:" + name));
                Pump(window);
                Assert.Contains(Texts(window), t => t == name);
                Assert.Equal(window.FontFamily, FindById<Button>(window, "Navigation:" + name).FontFamily);
                AssertButtonContentsCentered(window);
                Capture(window, $"{theme}-{width}-{name}");
                if (name == "Настройки")
                {
                    var scroll = FindById<ScrollViewer>(window, "PageScroll");
                    var tiles = (int)Math.Ceiling(scroll.Extent.Height / scroll.Viewport.Height);
                    for (var tile = 1; tile < tiles; tile++)
                    {
                        scroll.Offset = new Vector(0, tile * scroll.Viewport.Height * 0.9);
                        Pump(window);
                        AssertButtonContentsCentered(window);
                        Capture(window, $"{theme}-{width}-Настройки-{tile + 1}");
                    }
                }
            }
            Assert.Equal(0, app.Process.StartCalls);
            Assert.Equal(0, app.Process.StopCalls);
        }
        finally { health.Complete(Result(false, false)); window.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark", 1.25)]
    [InlineData("Light", 1.5)]
    [InlineData("Dark", 2.0)]
    public async Task ResizeAndDpiChangesKeepDashboardAndFocusUsable(string theme, double scale)
    {
        using var app = new TestApplication();
        app.Process.StatusOverride = new(ZapretRunState.Running, null, null, "general", "Running", "NotInstalled");
        var window = await OpenAsync(app, theme, 1440, 900);
        try
        {
            window.SetRenderScaling(scale);
            Pump(window);
            Assert.Equal(scale, window.RenderScaling);
            AssertMainLayout(window, 1440, 900);
            Capture(window, $"{theme}-1440-scale-{scale:0.##}");
            window.Width = 1200;
            window.Height = 800;
            Pump(window);
            AssertMainLayout(window, 1200, 800);
            var action = FindButton(window, "Остановить Zapret");
            Assert.True(action.Focus(NavigationMethod.Tab));
            Pump(window);
            Assert.True(action.IsKeyboardFocusWithin);
            var ring = action.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_FocusRing");
            Assert.NotEqual(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(ring.BorderBrush).Color);
            Capture(window, $"{theme}-1200-scale-{scale:0.##}-focus");
            window.Width = 1440;
            window.Height = 900;
            Pump(window);
            AssertMainLayout(window, 1440, 900);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public async Task FailedRetryDoesNotDisplayOldSuccessAsCurrent()
    {
        var health = new ControlledHealth();
        using var app = new TestApplication(connection: health);
        var window = await OpenAsync(app, "Dark", 1200, 800);
        try
        {
            var first = app.ViewModel.CheckConnectionAsync();
            health.Complete(Result(true, true));
            await first;
            health.Reset();
            var retry = app.ViewModel.CheckConnectionAsync();
            health.Fail(new IOException("Нет доступа к сети"));
            await retry;
            Pump(window);
            Assert.Equal("Нет доступа к сети", app.ViewModel.ConnectionError);
            Assert.DoesNotContain(Texts(window), t => t == "HTTPS доступен");
            Assert.Equal(2, Texts(window).Count(t => t == "Ошибка проверки"));
            Click(FindById<Button>(window, "ConnectionDisclosure:YouTube"));
            Assert.Contains(Texts(window), t => t == "Нет доступа к сети");
            Capture(window, "Dark-1200-check-exception");
        }
        finally { window.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData(ZapretRunState.External, "NotInstalled", "Внешний процесс")]
    [InlineData(ZapretRunState.Stopped, "Unknown", "Не подтверждено")]
    [InlineData(ZapretRunState.Stopped, "AccessDenied", "Не подтверждено")]
    public async Task UnownedOrUnconfirmedEngineNeverAppearsHealthy(ZapretRunState state, string service, string expected)
    {
        using var app = new TestApplication();
        app.Process.StatusOverride = new(state, 99, null, null, "Unknown", service);
        var window = await OpenAsync(app, "Light", 1200, 800);
        try
        {
            var metric = FindById<Border>(window, "ComponentStatus");
            Assert.Contains(metric.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == expected);
            Assert.DoesNotContain(metric.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Работает");
            Assert.False(FindButton(window, "Запустить Zapret").IsEffectivelyEnabled);
            Capture(window, "Light-1200-" + service + "-" + state);
        }
        finally { window.Hide(); }
    }

    private static async Task<MainWindow> OpenAsync(TestApplication app, string theme, int width, int height)
    {
        // Distribution checks are real file checks against this fixture's temporary directory.
        Directory.CreateDirectory(app.Distribution.BinDirectory);
        foreach (var file in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" })
            File.WriteAllText(Path.Combine(app.Distribution.BinDirectory, file), "UI test fixture, never executable");
        File.WriteAllText(Path.Combine(app.Distribution.Root, "service.bat"), "set LOCAL_VERSION=1.10.4");
        File.WriteAllText(Path.Combine(app.Distribution.ListsDirectory, "list-general.txt"), "example.test\n");
        await app.Settings.SaveAsync(new DesktopSettings { Theme = theme, SelectedStrategyId = "general", CheckUpdates = false, CheckConnectionAfterStart = false },
            TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel) { Width = width, Height = height };
        window.Show();
        await WaitAsync(() => app.ViewModel.Strategies.Count == 2 && app.ViewModel.Status is not null && !app.ViewModel.Busy);
        Pump(window);
        Assert.Empty(app.ViewModel.StartupWarnings);
        Assert.Equal("JetBrains Mono", new Typeface(window.FontFamily).GlyphTypeface.FamilyName);
        Assert.Equal(theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark, window.ActualThemeVariant);
        return window;
    }

    private static void AssertMainLayout(Window window, int width, int height)
    {
        var compact = width < 1280;
        var expectedX = compact ? 248 : 256;
        foreach (var id in new[] { "EngineControl", "ServiceAvailability", "ComponentStatus", "LaunchStrategy" })
        {
            var control = FindById<Border>(window, id);
            var point = control.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.X, expectedX - 1, expectedX + 1);
            Assert.InRange(point.X + control.Bounds.Width, width - (compact ? 29 : 33), width - (compact ? 27 : 31));
            Assert.True(point.Y >= 0 && point.Y + control.Bounds.Height <= height,
                $"{id} outside {width}×{height}: {point}, {control.Bounds}");
        }
        Assert.InRange(FindById<Border>(window, "EngineControl").TranslatePoint(default, window)!.Value.Y,
            compact ? 118 : 138, compact ? 122 : 142);
    }

    private static void DismissFeedback(Window window)
    {
        Pump(window);
        var dismiss = window.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Скрыть сообщение");
        Click(dismiss);
        Pump(window);
    }
    private static void AssertLaunchSectionLayout(Window window)
    {
        var restart = FindButton(window, "Применить выбор и перезапустить");
        Assert.DoesNotContain(restart.GetVisualAncestors().OfType<Control>(),
            c => AutomationProperties.GetAutomationId(c) == "LaunchStrategy");
        var frame = FindById<Border>(window, "LaunchStrategy");
        var framePosition = frame.TranslatePoint(default, window)!.Value;
        var picker = FindById<ComboBox>(window, "LaunchStrategyPicker");
        var buttonPosition = restart.TranslatePoint(default, window)!.Value;
        Assert.InRange(Math.Abs(framePosition.X - buttonPosition.X), 0, 0.5);
        Assert.InRange(buttonPosition.Y - framePosition.Y - frame.Bounds.Height, 19.5, 24.5);
        Assert.Contains(frame.GetLogicalDescendants(), c => c == picker);
        Assert.Contains(Texts(window), t => t == "Быстрые действия");
    }
    private static void AssertSectionInsets(Window window)
    {
        var titlebar = FindById<Grid>(window, "WindowTitleBar");
        Assert.Equal(255, Assert.IsAssignableFrom<ISolidColorBrush>(titlebar.Background).Color.A);
        Assert.Empty(titlebar.GetLogicalDescendants().OfType<TextBlock>());
        var scroll = FindById<ScrollViewer>(window, "PageScroll");
        Assert.True(scroll.TranslatePoint(default, window)!.Value.Y >= titlebar.Bounds.Height);
        var chromeButtons = titlebar.GetLogicalDescendants().OfType<Button>().ToArray();
        Assert.Equal(3, chromeButtons.Length);
        var centers = chromeButtons.Select(b => b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), titlebar)!.Value).ToArray();
        Assert.All(centers, center => Assert.InRange(Math.Abs(center.Y - titlebar.Bounds.Height / 2), 0, 0.5));
        Assert.Equal(centers[1].X - centers[0].X, centers[2].X - centers[1].X);

        foreach (var (id, label) in new[] { ("ServiceAvailability", "Доступность сервисов"),
                     ("ComponentStatus", "ДВИЖОК"), ("LaunchStrategy", "Стратегия для следующего запуска") })
        {
            var frame = FindById<Border>(window, id);
            var caption = frame.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == label);
            Assert.InRange(caption.TranslatePoint(default, frame)!.Value.X, 20.5, 21.5);
        }
        foreach (var (id, bottomText) in new[] { ("ServiceAvailability", "HTTPS-проверка не подтверждает воспроизведение видео YouTube и голосовую связь Discord."),
                     ("ComponentStatus", "winws.exe · PID 1234") })
        {
            var frame = FindById<Border>(window, id);
            var text = frame.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == bottomText);
            Assert.InRange(frame.Bounds.Height - text.TranslatePoint(default, frame)!.Value.Y - text.Bounds.Height, 20.5, 21.5);
        }
        var strategy = FindById<Border>(window, "LaunchStrategy");
        var picker = FindById<ComboBox>(window, "LaunchStrategyPicker");
        var position = picker.TranslatePoint(default, strategy)!.Value;
        Assert.InRange(strategy.Bounds.Width - position.X - picker.Bounds.Width, 20.5, 21.5);
    }
    private static void AssertButtonContentsCentered(Window window)
    {
        foreach (var button in Controls(window).OfType<ActionButton>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
        {
            var content = button.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ActionContent");
            var center = content.TranslatePoint(new Point(content.Bounds.Width / 2, content.Bounds.Height / 2), button)!.Value;
            if (button.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Center)
                Assert.InRange(Math.Abs(center.X - button.Bounds.Width / 2), 0, 0.6);
            else Assert.InRange(content.TranslatePoint(default, button)!.Value.X - button.Padding.Left - button.BorderThickness.Left, -0.6, 0.6);
            Assert.InRange(Math.Abs(center.Y - button.Bounds.Height / 2), 0, 0.6);
        }
    }
    private static void Capture(Window window, string name)
    {
        Pump(window);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
        var output = Environment.GetEnvironmentVariable("ZAPRET_UI_SCREENSHOTS") ??
            Path.Combine(AppContext.BaseDirectory, "TestResults", "engineering-ui");
        Directory.CreateDirectory(output);
        frame.Save(Path.Combine(output, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
    private static IEnumerable<Control> Controls(Window w) => w.GetLogicalDescendants().OfType<Control>();
    private static IEnumerable<string?> Texts(Window w) => Controls(w).OfType<TextBlock>().Select(t => t.Text);
    private static T FindById<T>(Window w, string id) where T : Control =>
        Controls(w).OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static Button FindButton(Window w, string text) => Controls(w).OfType<Button>().Single(b => b.Content as string == text);
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Dashboard did not settle");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private static ConnectionHealthResult Result(bool youtube, bool discord) => new(DateTimeOffset.UtcNow,
    [
        new("YouTube", "https://www.youtube.com/", youtube, youtube ? 200 : null, TimeSpan.FromMilliseconds(429),
            youtube ? "HTTP 200; сертификат TLS проверен тестовой службой" : "Тестовая ошибка TLS"),
        new("Discord", "https://discord.com/", discord, discord ? 200 : null, TimeSpan.FromMilliseconds(211),
            discord ? "HTTP 200; сертификат TLS проверен тестовой службой" : "Тестовая ошибка TLS")
    ]);
    private sealed class ControlledHealth : IConnectionHealthService
    {
        private TaskCompletionSource<ConnectionHealthResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConnectionHealthResult> CheckAsync(CancellationToken cancellationToken = default) => result.Task.WaitAsync(cancellationToken);
        public void Reset() => result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(ConnectionHealthResult value) => result.TrySetResult(value);
        public void Fail(Exception error) => result.TrySetException(error);
    }
}
