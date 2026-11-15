namespace Zapret.Desktop;

public sealed partial class MainViewModel
{
    internal void ResumeAfterFailedExit(Exception error)
    {
        exitRequested = false;
        ReportError("Не удалось завершить работу приложения", error);
    }
}
