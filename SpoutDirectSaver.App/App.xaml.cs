using System.Diagnostics;
using System.Windows;
using SpoutDirectSaver.App.Services;

namespace SpoutDirectSaver.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        WindowsScheduling.TryPromoteCurrentProcess(ProcessPriorityClass.High);
        base.OnStartup(e);
    }
}
