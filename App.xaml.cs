using System.Windows;
using System.Windows.Threading;

namespace WinNsfwScan;

public partial class App : System.Windows.Application {
	private TrayIconService? _trayIcon;
	private MainWindow? _mainWindow;
	private DetectionLoopService? _detectionLoopService;
	private OverlayWindow? _overlayWindow;

	protected override void OnStartup(StartupEventArgs e) {
		AppLogger.Info("App.OnStartup entered");
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		AppLogger.Info($"App logger initialized at {AppLogger.LogFilePath}");
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

		try {
			_trayIcon = new TrayIconService();

			var screenCaptureService = new ScreenCaptureService();
			var nudeNetClient = new NudeNetClient();
			_detectionLoopService = new DetectionLoopService(screenCaptureService, nudeNetClient, TimeSpan.FromSeconds(1));
			_detectionLoopService.NsfwDetected += OnNsfwDetected;
			_detectionLoopService.Start();

			_mainWindow = new MainWindow();
			AppLogger.Info("App.OnStartup completed");
		}
		catch(Exception ex) {
			AppLogger.Error("App.OnStartup failed", ex);
			throw;
		}
	}

	private void OnNsfwDetected() {
		AppLogger.Info("App.OnNsfwDetected entered");
		Dispatcher.InvokeAsync(ShowOverlay);
	}

	private void ShowOverlay() {
		AppLogger.Info("App.ShowOverlay entered");
		if(_overlayWindow != null && _overlayWindow.IsVisible)
		{
			AppLogger.Info("App.ShowOverlay skipped because overlay is already visible");
			return;
		}

		_overlayWindow = new OverlayWindow();
		_overlayWindow.Closed += (_, _) => _overlayWindow = null;
		_overlayWindow.Show();
		AppLogger.Info("App.ShowOverlay displayed overlay window");
	}

	public void ShowMainWindow() {
		AppLogger.Info("App.ShowMainWindow entered");
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

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
		AppLogger.Error("App.OnDispatcherUnhandledException", e.Exception);
	}

	private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) {
		if(e.ExceptionObject is Exception ex) {
			AppLogger.Error("App.OnUnhandledException", ex);
			return;
		}

		AppLogger.Error("App.OnUnhandledException with non-Exception payload");
	}

	protected override void OnExit(ExitEventArgs e) {
		AppLogger.Info("App.OnExit entered");
		if(_detectionLoopService != null) {
			_detectionLoopService.NsfwDetected -= OnNsfwDetected;
		}

		DispatcherUnhandledException -= OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

		_overlayWindow?.Close();
		_detectionLoopService?.Dispose();
		_trayIcon?.Dispose();
		AppLogger.Info("App.OnExit completed");
		base.OnExit(e);
	}
}