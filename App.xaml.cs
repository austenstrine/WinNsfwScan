using System.Windows;

namespace WinNsfwScan;

public partial class App : System.Windows.Application
{
	private TrayIconService? _trayIcon;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		_trayIcon = new TrayIconService();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_trayIcon?.Dispose();
		base.OnExit(e);
	}
}