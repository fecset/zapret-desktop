using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Zapret.Desktop;

internal static class Program
{
    private const string InstanceName = @"Local\ZapretDesktop-8D66C1A2-8476-4E1D-A5B3-4061B737B8DE";

    [STAThread]
    public static void Main(string[] args)
    {
        Mutex? instance = null;
        var ownsInstance = false;
        try
        {
            try { instance = new Mutex(true, InstanceName, out ownsInstance); }
            catch (UnauthorizedAccessException)
            {
                NotifyExistingInstance();
                return;
            }
            if (!ownsInstance)
            {
                NotifyExistingInstance();
                return;
            }
            if (!IsAdministrator())
            {
                instance.ReleaseMutex();
                ownsInstance = false;
                instance.Dispose();
                instance = null;
                RequestElevation();
                return;
            }
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-activate");
            using var stopListening = new CancellationTokenSource();
            var listener = Task.Run(() => ListenForActivation(activation, stopListening.Token));
            try { AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); }
            finally
            {
                stopListening.Cancel();
                activation.Set();
                listener.GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretDesktop");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "crash.log"), ex.ToString());
            throw;
        }
        finally
        {
            if (ownsInstance) instance?.ReleaseMutex();
            instance?.Dispose();
        }
    }

    private static bool IsAdministrator() => OperatingSystem.IsWindows() &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static void RequestElevation()
    {
        const uint yesNoWarning = 0x00000004 | 0x00000030;
        if (MessageBox(IntPtr.Zero,
                "Для запуска Zapret Desktop нужны права администратора. Перезапустить приложение с повышенными правами?",
                "Zapret Desktop", yesNoWarning) != 6) return;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось найти исполняемый файл приложения.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
            if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Не удалось найти сборку приложения."));
            Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, "Не удалось запустить приложение от имени администратора: " + ex.Message,
                "Zapret Desktop", 0x00000010);
        }
    }

    private static void NotifyExistingInstance()
    {
        try
        {
            using var activation = EventWaitHandle.OpenExisting(InstanceName + "-activate");
            activation.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            MessageBox(IntPtr.Zero, "Zapret Desktop уже запущен. Откройте его через значок в трее.",
                "Zapret Desktop", 0x00000040);
        }
    }

    private static void ListenForActivation(EventWaitHandle activation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!activation.WaitOne(500) || cancellationToken.IsCancellationRequested) continue;
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                {
                    window.Show();
                    window.Activate();
                }
            });
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string message, string title, uint type);
}
