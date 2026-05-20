using System.Windows;

namespace WinNsfwScan;

public partial class App : System.Windows.Application {
	private TrayIconService? _trayIcon;
	private MainWindow? _mainWindow;
	private DetectionLoopService? _detectionLoopService;
	private OverlayWindow? _overlayWindow;

	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		_trayIcon = new TrayIconService();

		var screenCaptureService = new ScreenCaptureService();
		var nudeNetClient = new NudeNetClient();
		_detectionLoopService = new DetectionLoopService(screenCaptureService, nudeNetClient, TimeSpan.FromSeconds(1));
		_detectionLoopService.NsfwDetected += OnNsfwDetected;
		_detectionLoopService.Start();

		_mainWindow = new MainWindow();
	}

	private void OnNsfwDetected() {
		Dispatcher.InvokeAsync(ShowOverlay);
	}

	private void ShowOverlay() {
		if(_overlayWindow != null && _overlayWindow.IsVisible)
			return;

		_overlayWindow = new OverlayWindow();
		_overlayWindow.Closed += (_, _) => _overlayWindow = null;
		_overlayWindow.Show();
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
		if(_detectionLoopService != null) {
			_detectionLoopService.NsfwDetected -= OnNsfwDetected;
		}

		_overlayWindow?.Close();
		_detectionLoopService?.Dispose();
		_trayIcon?.Dispose();
		base.OnExit(e);
	}
}