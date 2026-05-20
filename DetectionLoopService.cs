using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace WinNsfwScan;

public sealed class DetectionLoopService : IDisposable {
	private readonly ScreenCaptureService _screenCaptureService;
	private readonly NudeNetClient _nudeNetClient;
	private readonly TimeSpan _scanInterval;
	private const SKEncodedImageFormat TransportImageFormat = SKEncodedImageFormat.Jpeg;
	private const int TransportImageQuality = 90;

	public event Action<NudeNetDetection[]>? NsfwDetected;

	private record QuadrantInfo(int OffsetX, int OffsetY, string Name);

	private CancellationTokenSource? _cts;
	private Task? _loopTask;
	private bool _disposed;

	public DetectionLoopService(ScreenCaptureService screenCaptureService, NudeNetClient nudeNetClient, TimeSpan? scanInterval = null) {
		//AppLogger.Info("DetectionLoopService.ctor entered");
		_screenCaptureService = screenCaptureService;
		_nudeNetClient = nudeNetClient;
		_scanInterval = scanInterval ?? TimeSpan.FromSeconds(1);
		//AppLogger.Info($"DetectionLoopService.ctor configured interval={_scanInterval.TotalMilliseconds}ms");
	}

	public void Start() {
		//AppLogger.Info("DetectionLoopService.Start entered");
		if(_disposed)
			throw new ObjectDisposedException(nameof(DetectionLoopService));

		if(_loopTask != null && !_loopTask.IsCompleted) {
			//AppLogger.Info("DetectionLoopService.Start skipped because loop already running");
			return;
		}

		_cts = new CancellationTokenSource();
		_loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
		//AppLogger.Info("DetectionLoopService.Start scheduled background loop task");
	}

	public async Task StopAsync() {
		//AppLogger.Info("DetectionLoopService.StopAsync entered");
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
		//AppLogger.Info("DetectionLoopService.StopAsync completed");
	}

	private async Task RunLoopAsync(CancellationToken cancellationToken) {
		//AppLogger.Info("DetectionLoopService.RunLoopAsync entered");
		//AppLogger.Info("DetectionLoopService.RunLoopAsync started");
		long cycleNumber = 0;

		while(!cancellationToken.IsCancellationRequested) {
			cycleNumber++;
			var cycleSw = Stopwatch.StartNew();
			long captureMs = 0;
			long encodeMs = 0;
			long detectMs = 0;
			int encodedBytes = 0;
			string encodedFormat = "none";
			int width = 0;
			int height = 0;
			bool hadScreenshot = false;
			NudeNetDetection[]? nsfwDetections = null;
			int allDetectionCount = 0;

			try {
				var captureSw = Stopwatch.StartNew();
				using var screenshot = _screenCaptureService.CapturePrimaryScreen();
				captureSw.Stop();
				captureMs = captureSw.ElapsedMilliseconds;

				if(screenshot != null) {
					hadScreenshot = true;
					width = screenshot.Width;
					height = screenshot.Height;

					var encodeSw = Stopwatch.StartNew();

					// Prepare full screen + 4 quadrants
					var quadrantInfos = new[] {
						new QuadrantInfo(0, 0, "full"),
						new QuadrantInfo(0, 0, "quad-tl"),
						new QuadrantInfo(width / 2, 0, "quad-tr"),
						new QuadrantInfo(0, height / 2, "quad-bl"),
						new QuadrantInfo(width / 2, height / 2, "quad-br"),
					};

					var imagesToDetect = new List<(byte[] bytes, string fileName, QuadrantInfo info)>();

					// Encode full screen
					byte[] fullScreenBytes = EncodeForTransport(screenshot);
					imagesToDetect.Add((fullScreenBytes, "screen-full.jpg", quadrantInfos[0]));

					// Extract and encode quadrants
					int quadWidth = width / 2;
					int quadHeight = height / 2;

					for (int i = 1; i < 5; i++) {
						var info = quadrantInfos[i];
						using (var quadBitmap = new SKBitmap(quadWidth, quadHeight)) {
							using (var canvas = new SKCanvas(quadBitmap)) {
								var source = new SKRect(info.OffsetX, info.OffsetY, info.OffsetX + quadWidth, info.OffsetY + quadHeight);
								var dest = new SKRect(0, 0, quadWidth, quadHeight);
								canvas.DrawBitmap(screenshot, source, dest);
							}
							byte[] quadBytes = EncodeForTransport(quadBitmap);
							imagesToDetect.Add((quadBytes, $"screen-{info.Name}.jpg", info));
						}
					}

					encodeSw.Stop();
					encodeMs = encodeSw.ElapsedMilliseconds;
					encodedBytes = imagesToDetect.Sum(x => x.bytes.Length);
					encodedFormat = "jpeg";

					// Detect on all 5 images in parallel
					var detectSw = Stopwatch.StartNew();
					var detectTasks = imagesToDetect
						.Select((item, idx) => DetectQuadrantAsync(item.bytes, item.fileName, item.info, idx))
						.ToList();

					var detectionResults = await Task.WhenAll(detectTasks).ConfigureAwait(false);
					detectSw.Stop();
					detectMs = detectSw.ElapsedMilliseconds;

					// Consolidate and adjust coordinates
					var allDetections = new List<NudeNetDetection>();
					for (int i = 0; i < detectionResults.Length; i++) {
						var (detections, quadInfo) = detectionResults[i];
						
						// Adjust coordinates for quadrants (not needed for full screen since offset is 0,0)
						foreach (var detection in detections) {
							var adjusted = new NudeNetDetection(
								detection.Class,
								detection.Score,
								detection.X + quadInfo.OffsetX,
								detection.Y + quadInfo.OffsetY,
								detection.Width,
								detection.Height
							);
							allDetections.Add(adjusted);
						}
					}

					allDetectionCount = allDetections.Count;
					nsfwDetections = allDetections.Where(d => NsfwClassifier.IsNsfwClass(d.Class)).ToArray();

					// Log every raw detection so we can see what the model is actually returning.
					if(allDetections.Count > 0)
						AppLogger.Info($"DetectionLoopService detections: {string.Join(", ", allDetections.Select(d => $"{d.Class}:{d.Score:F2}({d.X},{d.Y},{d.Width}x{d.Height})" ))}");
					else
						AppLogger.Info("DetectionLoopService detections: none");

					if(nsfwDetections.Length > 0) {
						AppLogger.Info($"DetectionLoopService.RunLoopAsync NSFW detected {nsfwDetections.Length} regions from 5 detection passes");
						NsfwDetected?.Invoke(nsfwDetections);
					}
				}
			}
			catch(OperationCanceledException) {
				//AppLogger.Info("DetectionLoopService.RunLoopAsync canceled");
				throw;
			}
			catch(Exception ex) {
				AppLogger.Error("DetectionLoopService.RunLoopAsync error", ex);
			}
			finally {
				cycleSw.Stop();
				AppLogger.Info(
					$"Benchmark cycle={cycleNumber} total={cycleSw.ElapsedMilliseconds}ms capture={captureMs}ms encode={encodeMs}ms detect={detectMs}ms screenshot={(hadScreenshot ? "yes" : "no")} size={width}x{height} bytes={encodedBytes} format={encodedFormat} quality={TransportImageQuality} nsfw={(nsfwDetections?.Length ?? 0)}/{allDetectionCount} detections=5x(full+quadrants)" 
				);
			}

			await Task.Delay(_scanInterval, cancellationToken).ConfigureAwait(false);
		}

		AppLogger.Info("DetectionLoopService.RunLoopAsync stopped");
	}

	private async Task<(NudeNetDetection[] detections, QuadrantInfo info)> DetectQuadrantAsync(byte[] imageBytes, string fileName, QuadrantInfo quadInfo, int serverIndex) {
		try {
			var detections = await _nudeNetClient.DetectAsync(imageBytes, fileName, serverIndex).ConfigureAwait(false);
			return (detections, quadInfo);
		}
		catch (Exception ex) {
			AppLogger.Error($"DetectionLoopService.DetectQuadrantAsync error for {quadInfo.Name} on server {serverIndex}", ex);
			return (Array.Empty<NudeNetDetection>(), quadInfo);
		}
	}

	private static byte[] EncodeForTransport(SKBitmap bitmap) {
		//AppLogger.Info("DetectionLoopService.EncodeForTransport entered");
		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(TransportImageFormat, TransportImageQuality);
		if(data == null) {
			throw new InvalidOperationException("Failed to encode screenshot for transport.");
		}

		return data.ToArray();
	}

	public void Dispose() {
		//AppLogger.Info("DetectionLoopService.Dispose entered");
		if(_disposed)
			return;

		_disposed = true;

		StopAsync().GetAwaiter().GetResult();
		_nudeNetClient.Dispose();
		AppLogger.Info("DetectionLoopService.Dispose completed");
	}
}