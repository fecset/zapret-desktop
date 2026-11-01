using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Zapret.Desktop.Tests;

internal sealed class PreviewGalleryWindow : Window
{
    private readonly ComboBox engine = Picker(PreviewCatalog.Engines, "PreviewEngine");
    private readonly ComboBox connection = Picker(PreviewCatalog.Connections, "PreviewConnection");
    private readonly ComboBox updates = Picker(PreviewCatalog.Updates, "PreviewUpdates");
    private readonly ComboBox theme = new() { ItemsSource = new[] { "Тёмная", "Светлая" }, SelectedIndex = 0, Height = 44 };
    private readonly CheckBox compact = new() { Content = "Компактное окно — 1200 × 800", IsChecked = true };
    private readonly CheckBox openUpdates = new() { Content = "Сразу открыть раздел обновлений" };
    private readonly TextBlock message = new() { Text = "Выберите состояния и нажмите «Показать».", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Zapret.Desktop.ActionButton apply = new() { Content = "Показать состояние", Height = 44,
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private bool closing;
    private bool allowClose;
    private TaskCompletionSource? selectionCompletion;
    public PreviewSession? Session { get; private set; }

    public PreviewGalleryWindow(bool light = false, bool compactWindow = true)
    {
        if (!Application.Current!.Styles.OfType<StyleInclude>().Any(s => s.Source?.OriginalString.EndsWith("Engineering.axaml") == true))
            Application.Current.Styles.Add(new StyleInclude(new Uri("avares://Zapret.Desktop/")) {
                Source = new Uri("avares://Zapret.Desktop/Styles/Engineering.axaml") });
        Classes.Add("engineering");
        Title = "Состояния — Zapret Desktop";
        Width = 440; Height = 820; MinWidth = 400; MinHeight = 620;
        FontSize = 13;
        theme.SelectedIndex = light ? 1 : 0;
        compact.IsChecked = compactWindow;
        var form = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        form.Children.Add(new TextBlock { Text = "Состояния интерфейса", FontSize = 21, FontWeight = FontWeight.Medium });
        form.Children.Add(new TextBlock { Text = "Тестовые данные. Работающий Zapret и ваши настройки не меняются.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        AddField(form, "Zapret", engine);
        AddField(form, "Доступность сервисов", connection);
        AddField(form, "Обновления", updates);
        AddField(form, "Тема", theme);
        form.Children.Add(compact);
        form.Children.Add(openUpdates);
        apply.Classes.Add("engineering");
        apply.Padding = new Thickness(16, 0);
        apply.CornerRadius = new CornerRadius(5);
        AutomationProperties.SetAutomationId(apply, "ShowPreviewState");
        apply.Click += async (_, _) => await ShowSelectionAsync();
        form.Children.Add(apply);
        form.Children.Add(message);
        form.Children.Add(new TextBlock { Text = "В окне приложения можно нажимать кнопки, раскрывать результаты, переключать разделы и менять размер. Чтобы вернуть выбранный сценарий, нажмите «Показать состояние» ещё раз.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Content = new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        ApplyTheme(light);
        Opened += async (_, _) => await ShowSelectionAsync();
        Closing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            apply.IsEnabled = false;
            try
            {
                if (selectionCompletion is { } pending) await pending.Task;
                if (Session is not null) await Session.DisposeAsync();
                allowClose = true;
                Close();
            }
            catch (Exception ex) { message.Text = "Не удалось закрыть предпросмотр: " + ex.Message; closing = false; apply.IsEnabled = true; }
        };
    }

    public async Task ShowSelectionAsync()
    {
        if (apply.IsBusy || closing) return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        selectionCompletion = completed;
        apply.IsBusy = true;
        message.Text = "Открываем выбранные состояния…";
        try
        {
            if (Session is not null) await Session.DisposeAsync();
            Session = null;
            var config = new PreviewConfiguration(((PreviewChoice<PreviewEngine>)engine.SelectedItem!).Value,
                ((PreviewChoice<PreviewConnection>)connection.SelectedItem!).Value,
                ((PreviewChoice<PreviewUpdates>)updates.SelectedItem!).Value,
                theme.SelectedIndex == 1 ? "Light" : "Dark", compact.IsChecked == true, openUpdates.IsChecked == true);
            Session = await PreviewSession.OpenAsync(config, text => message.Text = text);
            Icon = Session.Window.Icon;
            ApplyTheme(config.Theme == "Light");
            var screen = Screens.ScreenFromWindow(this);
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                Position = new PixelPoint(area.X + 12, area.Y + 12);
                Session.Window.Position = new PixelPoint(area.X + (int)((Width + 24) * RenderScaling), area.Y + 12);
            }
            Session.Window.Activate();
            message.Text = "Сценарий открыт. Все версии, PID и результаты — примеры.";
        }
        catch (Exception ex) { message.Text = "Не удалось открыть сценарий: " + ex.Message; }
        finally { apply.IsBusy = false; completed.TrySetResult(); }
    }

    private void ApplyTheme(bool light)
    {
        RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        IBrush Brush(string key) => Application.Current!.TryGetResource("Engineering." + key, RequestedThemeVariant, out var value)
            && value is IBrush brush ? brush : throw new InvalidOperationException("Missing preview token: " + key);
        Background = Brush("canvas"); Foreground = Brush("text");
        apply.Background = Brush("primary"); apply.Foreground = Brush("onPrimary");
        message.Foreground = Brush("secondary");
    }
    private static ComboBox Picker<T>(IReadOnlyList<PreviewChoice<T>> choices, string id)
    {
        var picker = new ComboBox { ItemsSource = choices, SelectedIndex = 0, Height = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        AutomationProperties.SetAutomationId(picker, id);
        return picker;
    }
    private static void AddField(StackPanel form, string label, Control field)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Medium });
        section.Children.Add(field);
        AutomationProperties.SetName(field, label);
        form.Children.Add(section);
    }
}
