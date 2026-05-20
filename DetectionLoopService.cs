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

	private CancellationTokenSource? _cts;
	private Task? _loopTask;
	private bool _disposed;

	public DetectionLoopService(ScreenCaptureService screenCaptureService, NudeNetClient nudeNetClient, TimeSpan? scanInterval = null) {
		_screenCaptureService = screenCaptureService;
		_nudeNetClient = nudeNetClient;
		_scanInterval = scanInterval ?? TimeSpan.FromSeconds(1);
	}

	public void Start() {
		if(_disposed)
			throw new ObjectDisposedException(nameof(DetectionLoopService));

		if(_loopTask != null && !_loopTask.IsCompleted)
			return;

		_cts = new CancellationTokenSource();
		_loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
	}

	public async Task StopAsync() {
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
	}

	private async Task RunLoopAsync(CancellationToken cancellationToken) {
		Console.WriteLine("[DetectionLoop] Started");

		while(!cancellationToken.IsCancellationRequested) {
			try {
				using var screenshot = _screenCaptureService.CapturePrimaryScreen();
				if(screenshot != null) {
					byte[] imageBytes = EncodeToPng(screenshot);
					bool isNsfw = await _nudeNetClient.IsNsfwAsync(imageBytes, "screen.png").ConfigureAwait(false);

					if(isNsfw) {
						Console.WriteLine("[DetectionLoop] NSFW content detected");
					}
				}
			}
			catch(OperationCanceledException) {
				throw;
			}
			catch(Exception ex) {
				Console.WriteLine($"[DetectionLoop] Error: {ex.Message}");
			}

			await Task.Delay(_scanInterval, cancellationToken).ConfigureAwait(false);
		}

		Console.WriteLine("[DetectionLoop] Stopped");
	}

	private static byte[] EncodeToPng(SKBitmap bitmap) {
		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(SKEncodedImageFormat.Png, 90);
		using var stream = new MemoryStream();
		data.SaveTo(stream);
		return stream.ToArray();
	}

	public void Dispose() {
		if(_disposed)
			return;

		_disposed = true;

		StopAsync().GetAwaiter().GetResult();
		_nudeNetClient.Dispose();
	}
}