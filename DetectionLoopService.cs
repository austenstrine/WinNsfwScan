using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace WinNsfwScan;

public sealed class DetectionLoopService : IDisposable {
	private readonly ScreenCaptureService _screenCaptureService;
	private readonly NudeNetClient _nudeNetClient;
	private readonly TimeSpan _scanInterval;

	public event Action? NsfwDetected;

	private CancellationTokenSource? _cts;
	private Task? _loopTask;
	private bool _disposed;

	public DetectionLoopService(ScreenCaptureService screenCaptureService, NudeNetClient nudeNetClient, TimeSpan? scanInterval = null) {
		AppLogger.Info("DetectionLoopService.ctor entered");
		_screenCaptureService = screenCaptureService;
		_nudeNetClient = nudeNetClient;
		_scanInterval = scanInterval ?? TimeSpan.FromSeconds(1);
		AppLogger.Info($"DetectionLoopService.ctor configured interval={_scanInterval.TotalMilliseconds}ms");
	}

	public void Start() {
		AppLogger.Info("DetectionLoopService.Start entered");
		if(_disposed)
			throw new ObjectDisposedException(nameof(DetectionLoopService));

		if(_loopTask != null && !_loopTask.IsCompleted) {
			AppLogger.Info("DetectionLoopService.Start skipped because loop already running");
			return;
		}

		_cts = new CancellationTokenSource();
		_loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
		AppLogger.Info("DetectionLoopService.Start scheduled background loop task");
	}

	public async Task StopAsync() {
		AppLogger.Info("DetectionLoopService.StopAsync entered");
		if(_cts == null)
			return;

		_cts.Cancel();

		if(_loopTask != null) {
			try {
				await _loopTask.ConfigureAwait(false);
			}
			catch(OperationCanceledException) {
				// Expected when stopping.
			}
		}

		_cts.Dispose();
		_cts = null;
		_loopTask = null;
		AppLogger.Info("DetectionLoopService.StopAsync completed");
	}

	private async Task RunLoopAsync(CancellationToken cancellationToken) {
		AppLogger.Info("DetectionLoopService.RunLoopAsync entered");
		AppLogger.Info("DetectionLoopService.RunLoopAsync started");

		while(!cancellationToken.IsCancellationRequested) {
			try {
				using var screenshot = _screenCaptureService.CapturePrimaryScreen();
				if(screenshot != null) {
					byte[] imageBytes = EncodeToPng(screenshot);
					bool isNsfw = await _nudeNetClient.IsNsfwAsync(imageBytes, "screen.png").ConfigureAwait(false);

					if(isNsfw) {
						AppLogger.Info("DetectionLoopService.RunLoopAsync NSFW detected");
						NsfwDetected?.Invoke();
					}
				}
			}
			catch(OperationCanceledException) {
				AppLogger.Info("DetectionLoopService.RunLoopAsync canceled");
				throw;
			}
			catch(Exception ex) {
				AppLogger.Error("DetectionLoopService.RunLoopAsync error", ex);
			}

			await Task.Delay(_scanInterval, cancellationToken).ConfigureAwait(false);
		}

		AppLogger.Info("DetectionLoopService.RunLoopAsync stopped");
	}

	private static byte[] EncodeToPng(SKBitmap bitmap) {
		AppLogger.Info("DetectionLoopService.EncodeToPng entered");
		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(SKEncodedImageFormat.Png, 90);
		using var stream = new MemoryStream();
		data.SaveTo(stream);
		return stream.ToArray();
	}

	public void Dispose() {
		AppLogger.Info("DetectionLoopService.Dispose entered");
		if(_disposed)
			return;

		_disposed = true;

		StopAsync().GetAwaiter().GetResult();
		_nudeNetClient.Dispose();
		AppLogger.Info("DetectionLoopService.Dispose completed");
	}
}