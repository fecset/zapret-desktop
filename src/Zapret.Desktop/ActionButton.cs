using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;

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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center };
            var spinner = new LoadingSpinner();
            spinner.Bind(IsVisibleProperty, new Binding(nameof(IsBusy)) { Source = this });
            spinner.Bind(LoadingSpinner.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
            row.Children.Add(spinner);
            row.Children.Add(new ContentControl { Content = value, VerticalAlignment = VerticalAlignment.Center });
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
