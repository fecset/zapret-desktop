using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Zapret.Desktop;

public sealed class ActionButton : Button
{
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<ActionButton, bool>(nameof(IsBusy));
    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    protected override Type StyleKeyOverride => typeof(Button);
    protected override bool IsEnabledCore => base.IsEnabledCore && !IsBusy;

    public ActionButton()
    {
        ContentTemplate = new FuncDataTemplate<object>((value, _) =>
        {
            var row = new StackPanel { Name = "ActionContent", Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Bind(HorizontalAlignmentProperty, new Binding(nameof(HorizontalContentAlignment)) { Source = this });
            row.Bind(VerticalAlignmentProperty, new Binding(nameof(VerticalContentAlignment)) { Source = this });
            var spinner = new LoadingSpinner();
            spinner.Bind(IsVisibleProperty, new Binding(nameof(IsBusy)) { Source = this });
            spinner.Bind(LoadingSpinner.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
            row.Children.Add(spinner);
            Control content;
            if (value is string label)
            {
                var text = new TextBlock { Name = "ActionLabel", Text = label, TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, LineHeight = EngineeringTheme.ControlLineHeight };
                text.Bind(TextBlock.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
                text.Bind(TextBlock.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
                text.Bind(TextBlock.FontWeightProperty, new Binding(nameof(FontWeight)) { Source = this });
                content = text;
            }
            else content = new ContentControl { Content = value, VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            // Icon-only actions show the spinner instead of squeezing both into a square button.
            if (value is not string)
                content.Bind(IsVisibleProperty, new Binding("!IsBusy") { Source = this });
            row.Children.Add(content);
            return row;
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty && Content is string label)
            AutomationProperties.SetName(this, label);
        if (change.Property == IsBusyProperty)
        {
            UpdateIsEffectivelyEnabled();
            AutomationProperties.SetHelpText(this, IsBusy ? "Действие выполняется" : null);
        }
    }
}
