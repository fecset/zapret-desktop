using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Zapret.Infrastructure;

namespace Zapret.Core.Tests;

public sealed class StartupServiceTests
{
    private const string Directory = @"C:\Apps\Zapret & Desktop";
    private const string Executable = Directory + @"\Zapret.Desktop.exe";
    private static WindowsStartupService Service(Registration registration) => new(registration, Executable, Directory);

    [Fact]
    public void MigratesEnabledLegacyStartupAndKeepsTheCurrentExecutable()
    {
        var registration = new Registration { HasLegacyEntry = true };
        var service = Service(registration);
        Assert.True(service.IsEnabled());
        Assert.True(service.MigrateLegacyRegistration());
        Assert.True(service.IsEnabled());
        Assert.False(registration.HasLegacyEntry);
        Assert.Equal(new StartupCommand(Executable, "", Directory), registration.Command);
        Assert.Equal(["register", "remove-legacy"], registration.Operations);
        Assert.False(service.MigrateLegacyRegistration());
        Assert.Equal(2, registration.Operations.Count);
    }

    [Fact]
    public void FailedMigrationPreservesLegacyStartupForRetry()
    {
        var registration = new Registration { HasLegacyEntry = true, FailRegistration = true };
        var service = Service(registration);
        Assert.Throws<InvalidOperationException>(() => service.MigrateLegacyRegistration());
        Assert.True(registration.HasLegacyEntry);
        Assert.True(service.IsEnabled());
        Assert.False(registration.IsTaskEnabled);
        registration.FailRegistration = false;
        Assert.True(service.MigrateLegacyRegistration());
        Assert.False(registration.HasLegacyEntry);
        Assert.True(registration.IsTaskEnabled);
    }

    [Fact]
    public void DoesNotEnableStartupWithoutAnExistingRegistration()
    {
        var registration = new Registration();
        var service = Service(registration);
        Assert.False(service.MigrateLegacyRegistration());
        Assert.False(service.IsEnabled());
        Assert.Empty(registration.Operations);
    }

    [Fact]
    public void DisablingRemovesBothKindsOfRegistration()
    {
        var registration = new Registration { HasLegacyEntry = true, IsTaskEnabled = true };
        var service = Service(registration);
        service.SetEnabled(false);
        Assert.False(service.IsEnabled());
        Assert.Equal(["delete-task", "remove-legacy"], registration.Operations);
        service.SetEnabled(true);
        Assert.True(service.IsEnabled());
        Assert.False(registration.HasLegacyEntry);
    }

    [Fact]
    public void DisabledScheduledTaskIsReportedAsDisabled()
    {
        Assert.False(Service(new Registration { IsTaskEnabled = false }).IsEnabled());
    }

    [Fact]
    public void FailedRemovalKeepsBothRegistrationsAndReportsTheError()
    {
        var registration = new Registration { HasLegacyEntry = true, IsTaskEnabled = true, FailDeletion = true };
        Assert.Throws<InvalidOperationException>(() => Service(registration).SetEnabled(false));
        Assert.True(registration.HasLegacyEntry);
        Assert.True(registration.IsTaskEnabled);
        Assert.Equal(["delete-task"], registration.Operations);
    }

    [Fact]
    public void DotnetHostedStartupIncludesTheDesktopAssembly()
    {
        var registration = new Registration();
        var service = new WindowsStartupService(registration, @"C:\Program Files\dotnet\DOTNET.EXE", Directory);
        service.SetEnabled(true);
        Assert.Equal($"\"{Directory}\\Zapret.Desktop.dll\"", registration.Command!.Arguments);
        Assert.Equal(Directory, registration.Command.WorkingDirectory);
    }

    [Fact]
    public void TaskRunsInTheUsersDesktopWithElevationAndNoBatteryOrTimeLimit()
    {
        const string userId = "S-1-5-21-1-2-3-1001";
        var xml = XDocument.Parse(StartupTaskDefinition.BuildXml(userId, new(Executable, "", Directory)));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = xml.Root!;
        var principal = task.Element(ns + "Principals")!.Element(ns + "Principal")!;
        Assert.Equal(userId, principal.Element(ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(ns + "RunLevel")!.Value);
        var trigger = task.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!;
        Assert.Equal(userId, trigger.Element(ns + "UserId")!.Value);
        Assert.Equal("true", trigger.Element(ns + "Enabled")!.Value);
        var settings = task.Element(ns + "Settings")!;
        Assert.Equal("false", settings.Element(ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", settings.Element(ns + "ExecutionTimeLimit")!.Value);
        var action = task.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal(Executable, action.Element(ns + "Command")!.Value);
        Assert.Equal(Directory, action.Element(ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void WindowsTaskSchedulerAcceptsGeneratedXmlWithoutRegisteringATask()
    {
        if (!OperatingSystem.IsWindows()) return;
        var userId = WindowsIdentity.GetCurrent().User!.Value;
        object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        object? folder = null;
        object? result = null;
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            var command = new StartupCommand(Environment.ProcessPath!, "", AppContext.BaseDirectory);
            var xml = StartupTaskDefinition.BuildXml(userId, command);
            // TASK_VALIDATE_ONLY = 1; this validates the Windows schema without changing autostart.
            result = ((dynamic)folder).RegisterTask("ZapretDesktop-validate-" + Guid.NewGuid().ToString("N"),
                xml, 1, userId, null, 3, null);
        }
        finally
        {
            if (result is not null) Marshal.ReleaseComObject(result);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(service);
        }
    }

    [Fact]
    public void ReadingAndDeletingAMissingTaskAreIdempotent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scheduler = new StartupTaskScheduler();
        var name = "ZapretDesktop-missing-" + Guid.NewGuid().ToString("N");
        Assert.False(scheduler.IsEnabled(name));
        scheduler.Delete(name);
        Assert.False(scheduler.IsEnabled(name));
    }

    private sealed class Registration : IStartupRegistration
    {
        public bool HasLegacyEntry { get; set; }
        public bool IsTaskEnabled { get; set; }
        public bool FailRegistration { get; set; }
        public bool FailDeletion { get; set; }
        public StartupCommand? Command { get; private set; }
        public List<string> Operations { get; } = [];
        public void RegisterTask(StartupCommand command)
        {
            Operations.Add("register");
            if (FailRegistration) throw new InvalidOperationException("Access denied");
            Command = command;
            IsTaskEnabled = true;
        }
        public void DeleteTask()
        {
            Operations.Add("delete-task");
            if (FailDeletion) throw new InvalidOperationException("Access denied");
            IsTaskEnabled = false;
        }
        public void DeleteLegacyEntry() { Operations.Add("remove-legacy"); HasLegacyEntry = false; }
    }
}
