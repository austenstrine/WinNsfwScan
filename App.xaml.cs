using System.Windows;

namespace WinNsfwScan;

public partial class App : System.Windows.Application {
	private TrayIconService? _trayIcon;
	private MainWindow? _mainWindow;

	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		_trayIcon = new TrayIconService();

		_mainWindow = new MainWindow();
	}

	public void ShowMainWindow() {
		if(_mainWindow == null) {
			_mainWindow = new MainWindow();
		}

		if(_mainWindow.IsVisible) {
			_mainWindow.Hide();
		}
		else {
			_mainWindow.Show();
			_mainWindow.Activate();
		}
	}

	protected override void OnExit(ExitEventArgs e) {
		_trayIcon?.Dispose();
		base.OnExit(e);
	}
}