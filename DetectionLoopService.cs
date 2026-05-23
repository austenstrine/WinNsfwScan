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
	private const int TargetScanSize = 640;
	private const SKEncodedImageFormat TransportImageFormat = SKEncodedImageFormat.Jpeg;
	private const int TransportImageQuality = 90;

	public event Action<NudeNetDetection[]>? NsfwDetected;

	private record ScanRegionInfo(int OffsetX, int OffsetY, string Name);

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
		int nextRegionIndex = 0;

		while(!cancellationToken.IsCancellationRequested) {
			cycleNumber++;
			var cycleSw = Stopwatch.StartNew();
			long captureMs = 0;
			long encodeMs = 0;
			long detectMs = 0;
			int encodedBytes = 0;
			int width = 0;
			int height = 0;
			NudeNetDetection[]? nsfwDetections = null;
			int allDetectionCount = 0;
			(NudeNetDetection[] Detections, ScanRegionInfo RegionInfo, int Bytes, long SlotEncodeMs, long SlotDetectMs)? result = null;

			try {
				var captureSw = Stopwatch.StartNew();
				using var screenshot = _screenCaptureService.CapturePrimaryScreen();
				captureSw.Stop();
				captureMs = captureSw.ElapsedMilliseconds;

				if(screenshot != null) {
					width = screenshot.Width;
					height = screenshot.Height;
					int scanSize = Math.Min(TargetScanSize, Math.Min(width, height));

					var scanRegions = new[] {
						new ScanRegionInfo(0, 0, "top-left"),
						new ScanRegionInfo(Math.Max(0, width - scanSize), 0, "top-right"),
						new ScanRegionInfo(0, Math.Max(0, height - scanSize), "bottom-left"),
						new ScanRegionInfo(Math.Max(0, width - scanSize), Math.Max(0, height - scanSize), "bottom-right"),
						new ScanRegionInfo(Math.Max(0, (width - scanSize) / 2), Math.Max(0, (height - scanSize) / 2), "center"),
					};

					var activeRegion = scanRegions[nextRegionIndex];
					nextRegionIndex = (nextRegionIndex + 1) % scanRegions.Length;

					var encodeSw = Stopwatch.StartNew();
					var slotEncodeSw = Stopwatch.StartNew();
					using var regionBitmap = new SKBitmap(scanSize, scanSize);
					using (var canvas = new SKCanvas(regionBitmap)) {
						var source = new SKRect(activeRegion.OffsetX, activeRegion.OffsetY, activeRegion.OffsetX + scanSize, activeRegion.OffsetY + scanSize);
						var dest = new SKRect(0, 0, scanSize, scanSize);
						canvas.DrawBitmap(screenshot, source, dest);
					}
					byte[] imageBytes = EncodeForTransport(regionBitmap);
					slotEncodeSw.Stop();

					NudeNetDetection[] detections;
					var slotDetectSw = Stopwatch.StartNew();
					try {
						detections = await _nudeNetClient.DetectAsync(imageBytes, $"screen-{activeRegion.Name}.jpg", 0).ConfigureAwait(false);
					}
					catch (Exception ex) {
						AppLogger.Error($"DetectionLoopService detect error for {activeRegion.Name}", ex);
						detections = Array.Empty<NudeNetDetection>();
					}
					slotDetectSw.Stop();

					result = (
						Detections: detections,
						RegionInfo: activeRegion,
						Bytes: imageBytes.Length,
						SlotEncodeMs: slotEncodeSw.ElapsedMilliseconds,
						SlotDetectMs: slotDetectSw.ElapsedMilliseconds
					);

					encodeSw.Stop();
					encodeMs = encodeSw.ElapsedMilliseconds;
					encodedBytes = result.Value.Bytes;
					detectMs = result.Value.SlotDetectMs;

					// Consolidate and adjust coordinates
					var allDetections = new List<NudeNetDetection>();
					foreach (var detection in result.Value.Detections) {
						allDetections.Add(new NudeNetDetection(
							detection.Class,
							detection.Score,
							detection.X + result.Value.RegionInfo.OffsetX,
							detection.Y + result.Value.RegionInfo.OffsetY,
							detection.Width,
							detection.Height
						));
					}

					allDetectionCount = allDetections.Count;
					nsfwDetections = allDetections.Where(d => NsfwClassifier.IsNsfwClass(d.Class)).ToArray();

					//AppLogger.Info($"DetectionLoopService detections: {string.Join(", ", allDetections.Select(d => $"{d.Class}:{d.Score:F2}({d.X},{d.Y},{d.Width}x{d.Height})" ))}");

					if(nsfwDetections.Length > 0) {
						//AppLogger.Info($"DetectionLoopService.RunLoopAsync NSFW detected {nsfwDetections.Length} regions from 1 detection pass");
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
				// Benchmark cycle logs intentionally disabled while tuning thresholds.
			}

			await Task.Delay(_scanInterval, cancellationToken).ConfigureAwait(false);
		}

		//AppLogger.Info("DetectionLoopService.RunLoopAsync stopped");
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
		//AppLogger.Info("DetectionLoopService.Dispose completed");
	}
}