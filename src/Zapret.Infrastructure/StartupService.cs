using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;
using Zapret.Core;

namespace Zapret.Infrastructure;

public sealed class WindowsStartupService : IStartupService
{
    private readonly IStartupRegistration registration;
    private readonly StartupCommand command;

    public WindowsStartupService() : this(new WindowsStartupRegistration(),
        Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable."),
        AppContext.BaseDirectory) { }

    internal WindowsStartupService(IStartupRegistration registration, string executable, string directory)
    {
        this.registration = registration;
        command = StartupTaskDefinition.GetCommand(executable, directory);
    }

    public bool IsEnabled() => registration.HasLegacyEntry || registration.IsTaskEnabled;

    public void SetEnabled(bool enabled)
    {
        if (enabled) registration.RegisterTask(command);
        else registration.DeleteTask();
        // Keep the old entry if creating or deleting the task failed.
        registration.DeleteLegacyEntry();
    }

    public bool MigrateLegacyRegistration()
    {
        if (!registration.HasLegacyEntry) return false;
        SetEnabled(true);
        return true;
    }
}

internal sealed record StartupCommand(string Executable, string Arguments, string WorkingDirectory);

internal interface IStartupRegistration
{
    bool HasLegacyEntry { get; }
    bool IsTaskEnabled { get; }
    void RegisterTask(StartupCommand command);
    void DeleteTask();
    void DeleteLegacyEntry();
}

internal static class StartupTaskDefinition
{
    internal static StartupCommand GetCommand(string executable, string directory) => new(executable,
        Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? $"\"{Path.Combine(directory, "Zapret.Desktop.dll")}\"" : "", directory);

    internal static string BuildXml(string userId, StartupCommand command)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, params object[] content) => new(ns + name, content);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            Element("RegistrationInfo", Element("Description", "Запуск Zapret Desktop при входе в Windows.")),
            Element("Triggers", Element("LogonTrigger", Element("Enabled", "true"),
                Element("UserId", userId), Element("Delay", "PT5S"))),
            Element("Principals", Element("Principal", new XAttribute("id", "CurrentUser"),
                Element("UserId", userId), Element("LogonType", "InteractiveToken"),
                Element("RunLevel", "HighestAvailable"))),
            Element("Settings", Element("MultipleInstancesPolicy", "IgnoreNew"),
                Element("DisallowStartIfOnBatteries", "false"), Element("StopIfGoingOnBatteries", "false"),
                Element("StartWhenAvailable", "true"), Element("Enabled", "true"),
                Element("ExecutionTimeLimit", "PT0S")),
            Element("Actions", new XAttribute("Context", "CurrentUser"),
                Element("Exec", Element("Command", command.Executable), Element("Arguments", command.Arguments),
                    Element("WorkingDirectory", command.WorkingDirectory)))))
            .ToString(SaveOptions.DisableFormatting);
    }
}

internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZapretDesktop";
    private readonly string userId = WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("Не удалось определить пользователя для автозапуска.");
    private string TaskName => "ZapretDesktop-" + userId;

    public bool HasLegacyEntry
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public bool IsTaskEnabled
    {
        get
        {
            using var scheduler = new StartupTaskScheduler();
            return scheduler.IsEnabled(TaskName);
        }
    }

    public void RegisterTask(StartupCommand command)
    {
        using var scheduler = new StartupTaskScheduler();
        scheduler.Register(TaskName, StartupTaskDefinition.BuildXml(userId, command), userId);
    }

    public void DeleteTask()
    {
        using var scheduler = new StartupTaskScheduler();
        scheduler.Delete(TaskName);
    }

    public void DeleteLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal sealed class StartupTaskScheduler : IDisposable
{
    private const int FileNotFound = unchecked((int)0x80070002);
    private readonly object service;
    private readonly object folder;

    internal StartupTaskScheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Планировщик заданий недоступен.");
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
        }
        catch
        {
            Marshal.ReleaseComObject(service);
            throw;
        }
    }

    internal bool IsEnabled(string name)
    {
        object? task = null;
        try
        {
            task = ((dynamic)folder).GetTask(name);
            return ((dynamic)task).Enabled;
        }
        catch (Exception ex) when (ex.HResult == FileNotFound) { return false; }
        finally { if (task is not null) Marshal.ReleaseComObject(task); }
    }

    internal void Register(string name, string xml, string userId)
    {
        // TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3.
        object task = ((dynamic)folder).RegisterTask(name, xml, 6, userId, null, 3, null);
        Marshal.ReleaseComObject(task);
    }

    internal void Delete(string name)
    {
        try { ((dynamic)folder).DeleteTask(name, 0); }
        catch (Exception ex) when (ex.HResult == FileNotFound) { }
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(folder);
        Marshal.ReleaseComObject(service);
    }
}
