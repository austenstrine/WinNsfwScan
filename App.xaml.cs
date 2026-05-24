using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace WinNsfwScan;

public partial class App : System.Windows.Application {
	private const long BoxLifetimeCycles = 10;
	private const float BoxMergeIouThreshold = 0.25f;

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

	protected override void OnStartup(StartupEventArgs e) {
		//AppLogger.Info("App.OnStartup entered");
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		WindowsToastService.Initialize();

		AppLogger.Info($"App logger initialized at {AppLogger.LogFilePath}");
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

		try {
			_trayIcon = new TrayIconService();

			var screenCaptureService = new ScreenCaptureService();
			var nudeNetClient = new NudeNetClient();
			_detectionLoopService = new DetectionLoopService(screenCaptureService, nudeNetClient, TimeSpan.Zero);
			_detectionLoopService.NsfwDetected += OnNsfwDetected;
			_detectionLoopService.CycleCompleted += OnCycleCompleted;
			_detectionLoopService.Start();

			_mainWindow = new MainWindow();

			_overlayWindow = new OverlayWindow();
			_overlayWindow.Show();

			WindowsToastService.TryShow("WinNsfwScan", $"All detection servers are ready ({nudeNetClient.ServerCount} online).");

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
		Dispatcher.InvokeAsync(() => PruneExpiredBoxes(cycleNumber));
	}

	private void UpdateTrackedBoxes(long cycleNumber, NudeNetDetection[] detections) {
		if(detections.Any(d => NsfwClassifier.IsHardNsfwDetection(d.Class, d.Score))) {
			AppLogger.Info($"App.UpdateTrackedBoxes hard NSFW trigger cycle={cycleNumber} detections={detections.Length}");
			_trackedBoxes.Clear();
			_overlayWindow?.ShowFullScreenBlock();
			return;
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