using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Zapret.Core;

namespace Zapret.Desktop.Tests;

public sealed class PageDraftTests
{
    [AvaloniaFact]
    public async Task ListFileSearchAndEntrySurviveBothLayoutBreakpoints()
    {
        using var app = new TestApplication();
        await File.WriteAllTextAsync(Path.Combine(app.Distribution.ListsDirectory, "list-general.txt"), "example.test\n", TestContext.Current.CancellationToken);
        await app.Settings.SaveAsync(new DesktopSettings { CheckUpdates = false, MinimizeToTray = false }, TestContext.Current.CancellationToken);
        var window = new MainWindow(app.ViewModel, exitApplication: () => { });
        window.Show(); await window.InitializeAsync();
        try
        {
            Find<Button>("Navigation:Списки").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForListAsync();
            Find<ComboBox>("ListFile").SelectedItem = "ipset-all.txt";
            await WaitForListAsync();
            Find<TextBox>("ListQuery").Text = "1.2.3";
            Find<TextBox>("ListEntry").Text = "unfinished subnet";
            foreach (var width in new[] { 1200, 1099, 1440 })
            {
                window.Width = width;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                await WaitForListAsync();
                Assert.Equal("ipset-all.txt", Find<ComboBox>("ListFile").SelectedItem);
                Assert.Equal("1.2.3", Find<TextBox>("ListQuery").Text);
                Assert.Equal("unfinished subnet", Find<TextBox>("ListEntry").Text);
            }
            Assert.Equal("1.2.3.0/24\n", await File.ReadAllTextAsync(Path.Combine(app.Distribution.ListsDirectory, "ipset-all.txt"), TestContext.Current.CancellationToken));
        }
        finally { await window.ExitAsync(); window.Close(); }

        T Find<T>(string id) where T : Control => window.GetLogicalDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetAutomationId(control) == id);
        async Task WaitForListAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (app.ViewModel.Busy)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("List search did not complete.");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        }
    }
}
