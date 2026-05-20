using System.Windows;

namespace WinNsfwScan;

public partial class App : System.Windows.Application {
	private TrayIconService? _trayIcon;
	private MainWindow? _mainWindow;
	private DetectionLoopService? _detectionLoopService;

	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		_trayIcon = new TrayIconService();

		var screenCaptureService = new ScreenCaptureService();
		var nudeNetClient = new NudeNetClient();
		_detectionLoopService = new DetectionLoopService(screenCaptureService, nudeNetClient, TimeSpan.FromSeconds(1));
		_detectionLoopService.Start();

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
		_detectionLoopService?.Dispose();
		_trayIcon?.Dispose();
		base.OnExit(e);
	}
}