using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace WinNsfwScan;

public partial class App : System.Windows.Application {
	private const long BoxLifetimeCycles = 10;
	private const float BoxMergeIouThreshold = 0.25f;
	private static readonly TimeSpan MinimizeCooldown = TimeSpan.FromSeconds(10);

	private sealed class TrackedBox {
		public NudeNetDetection Detection { get; set; }
		public long LastSeenCycle { get; set; }

		public TrackedBox(NudeNetDetection detection, long lastSeenCycle) {
			Detection = detection;
			LastSeenCycle = lastSeenCycle;
		}
	}

	private TrayIconService? _trayIcon;
	private MainWindow? _mainWindow;
	private DetectionLoopService? _detectionLoopService;
	private OverlayWindow? _overlayWindow;
	private readonly List<TrackedBox> _trackedBoxes = new();
	private DateTime _lastWindowMinimizeUtc = DateTime.MinValue;

	protected override void OnStartup(StartupEventArgs e) {
		//AppLogger.Info("App.OnStartup entered");
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		// Watchdog mode: no UI — just monitor a PID and restart it when it exits.
		if(WatchdogService.IsWatchdogMode(e.Args, out int targetPid)) {
			AppLogger.Info($"App.OnStartup watchdog mode for pid={targetPid}");
			new Thread(() => {
				WatchdogService.RunAsWatchdog(targetPid);
				Environment.Exit(0);
			}) { Name = "WatchdogThread", IsBackground = false }.Start();
			return;
		}

		WindowsToastService.Initialize();

		AppLogger.Info($"App logger initialized at {AppLogger.LogFilePath}");
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

		// Start resurrection guard, unless the user already completed the 30-min cooldown.
		// In that case, delete the stale flag so protection is active again next time.
		if(WatchdogService.IsProtectionDisabled()) {
			WatchdogService.DeleteDisableFile();
			AppLogger.Info("App.OnStartup protection was disabled (cooldown expired), clearing flag");
		} else {
			WatchdogService.EnsureWatchdog();
		}

		try {
			_trayIcon = new TrayIconService();

			var screenCaptureService = new ScreenCaptureService();
			var nudeNetClient = new NudeNetClient();
			_detectionLoopService = new DetectionLoopService(screenCaptureService, nudeNetClient, TimeSpan.Zero);
			_detectionLoopService.NsfwDetected += OnNsfwDetected;
			_detectionLoopService.CycleCompleted += OnCycleCompleted;
			_detectionLoopService.Start();

			_mainWindow = new MainWindow();
			ShowMainWindow();

			_overlayWindow = new OverlayWindow();
			_overlayWindow.Show();

			WindowsToastService.TryShow("WinNsfwScan", $"All detection servers are ready ({nudeNetClient.ServerCount} EraX online).");

			AppLogger.Info("App.OnStartup completed");
		}
		catch(Exception ex) {
			AppLogger.Error("App.OnStartup failed", ex);
			throw;
		}
	}

	private void OnNsfwDetected(long cycleNumber, NudeNetDetection[] detections) {
		//AppLogger.Info($"App.OnNsfwDetected entered detections={detections.Length}");
		Dispatcher.InvokeAsync(() => UpdateTrackedBoxes(cycleNumber, detections));
	}

	private void OnCycleCompleted(long cycleNumber) {
		Dispatcher.InvokeAsync(() => {
			PruneExpiredBoxes(cycleNumber);
			SyncOverlayLayering();
		});
	}

	private void UpdateTrackedBoxes(long cycleNumber, NudeNetDetection[] detections) {
		var hardDetections = detections
			.Where(d => NsfwClassifier.IsHardNsfwDetection(d.Class, d.Score))
			.OrderByDescending(d => d.Score)
			.ToArray();

		if(hardDetections.Length > 0) {
			AppLogger.Info($"App.UpdateTrackedBoxes hard NSFW trigger cycle={cycleNumber} detections={detections.Length}");
			TryMinimizeWindowForDetection(hardDetections[0]);
			_mainWindow?.ActivateHardBlock(TimeSpan.FromSeconds(10));
			SyncOverlayLayering();
		}

		bool addedAny = false;

		foreach(var detection in detections) {
			if(!TryRefreshTrackedBox(detection, cycleNumber)) {
				_trackedBoxes.Add(new TrackedBox(detection, cycleNumber));
				addedAny = true;
			}
		}

		if(addedAny) {
			RenderTrackedBoxes();
		}
	}

	private void PruneExpiredBoxes(long cycleNumber) {
		if(_trackedBoxes.Count == 0)
			return;

		int removed = _trackedBoxes.RemoveAll(box => cycleNumber - box.LastSeenCycle >= BoxLifetimeCycles);
		if(removed > 0) {
			RenderTrackedBoxes();
		}
	}

	private bool TryRefreshTrackedBox(NudeNetDetection detection, long cycleNumber) {
		foreach(var trackedBox in _trackedBoxes) {
			if(!string.Equals(trackedBox.Detection.Class, detection.Class, StringComparison.OrdinalIgnoreCase))
				continue;

			if(GetIntersectionOverUnion(trackedBox.Detection, detection) < BoxMergeIouThreshold)
				continue;

			// Keep existing box geometry stable and only refresh expiration.
			trackedBox.LastSeenCycle = cycleNumber;
			return true;
		}

		return false;
	}

	private void RenderTrackedBoxes() {
		_overlayWindow?.ReplaceBoxes(_trackedBoxes.Select(box => box.Detection).ToArray());
	}

	private void SyncOverlayLayering() {
		if(_overlayWindow == null || _mainWindow == null)
			return;

		if(_mainWindow.IsHardBlockActive && _mainWindow.WindowHandle != IntPtr.Zero) {
			_overlayWindow.SetPreferredAboveWindow(_mainWindow.WindowHandle);
		}
		else {
			_overlayWindow.SetPreferredAboveWindow(IntPtr.Zero);
		}
	}

	private void TryMinimizeWindowForDetection(NudeNetDetection detection) {
		DateTime now = DateTime.UtcNow;
		if(now - _lastWindowMinimizeUtc < MinimizeCooldown)
			return;

		int centerX = detection.X + Math.Max(0, detection.Width / 2);
		int centerY = detection.Y + Math.Max(0, detection.Height / 2);

		var excludedWindows = new List<IntPtr>();
		if(_mainWindow != null && _mainWindow.WindowHandle != IntPtr.Zero)
			excludedWindows.Add(_mainWindow.WindowHandle);
		if(_overlayWindow != null && _overlayWindow.WindowHandle != IntPtr.Zero)
			excludedWindows.Add(_overlayWindow.WindowHandle);

		if(WindowMinimizerService.TryMinimizeWindowAtPoint(centerX, centerY, excludedWindows, out var minimizedWindow, out var title)) {
			_lastWindowMinimizeUtc = now;
			string safeTitle = string.IsNullOrWhiteSpace(title) ? "<untitled>" : title;
			AppLogger.Info($"App minimized window hwnd=0x{minimizedWindow.ToInt64():X} title={safeTitle} at=({centerX},{centerY})");
		}
	}

	private static float GetIntersectionOverUnion(NudeNetDetection a, NudeNetDetection b) {
		int aRight = a.X + a.Width;
		int aBottom = a.Y + a.Height;
		int bRight = b.X + b.Width;
		int bBottom = b.Y + b.Height;

		int intersectionLeft = Math.Max(a.X, b.X);
		int intersectionTop = Math.Max(a.Y, b.Y);
		int intersectionRight = Math.Min(aRight, bRight);
		int intersectionBottom = Math.Min(aBottom, bBottom);

		int intersectionWidth = Math.Max(0, intersectionRight - intersectionLeft);
		int intersectionHeight = Math.Max(0, intersectionBottom - intersectionTop);
		int intersectionArea = intersectionWidth * intersectionHeight;
		if(intersectionArea == 0)
			return 0f;

		int unionArea = (a.Width * a.Height) + (b.Width * b.Height) - intersectionArea;
		if(unionArea <= 0)
			return 0f;

		return (float)intersectionArea / unionArea;
	}

	public void ShowMainWindow() {
		//AppLogger.Info("App.ShowMainWindow entered");
		if(_mainWindow == null) {
			_mainWindow = new MainWindow();
		}

		if(!_mainWindow.IsWebUiLoaded) {
			if(!_mainWindow.IsVisible) {
				_mainWindow.Show();
			}

			_mainWindow.Activate();
			return;
		}

		if(_mainWindow.IsHardBlockActive) {
			_mainWindow.ActivateHardBlock(TimeSpan.FromSeconds(10));
			return;
		}

		if(_mainWindow.IsVisible) {
			_mainWindow.Activate();
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
		//AppLogger.Info("App.OnExit entered");
		if(_detectionLoopService != null) {
			_detectionLoopService.NsfwDetected -= OnNsfwDetected;
			_detectionLoopService.CycleCompleted -= OnCycleCompleted;
		}

		DispatcherUnhandledException -= OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

		_overlayWindow?.Close();
		_detectionLoopService?.Dispose();
		if (_detectionLoopService?.AverageCycleMs is double avg) {
			AppLogger.Info($"Shutdown avgCycleMs={avg:F1}");
		}
		_trayIcon?.Dispose();
		//AppLogger.Info("App.OnExit completed")
		base.OnExit(e);
	}
}