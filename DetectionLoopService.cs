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
	private const int TransportImageQuality = 80;
	// Pre-downscale crops to the model's input size before JPEG encoding.
	// Matches the resolution the server backend is launched with (640m.onnx @ 640).
	private const int InferenceSize = 640;

	public event Action<long, NudeNetDetection[]>? NsfwDetected;
	public event Action<long>? CycleCompleted;

	private record ScanRegionInfo(int OffsetX, int OffsetY, string Name);

	private CancellationTokenSource? _cts;
	private Task? _loopTask;
	private bool _disposed;

	private long _totalCycleMs;
	private long _measuredCycleCount;

	/// <summary>Average cycle duration in milliseconds, excluding the first warm-up cycle. Null if fewer than two cycles have completed.</summary>
	public double? AverageCycleMs => _measuredCycleCount > 0
		? (double)_totalCycleMs / _measuredCycleCount
		: null;

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
			bool hadScreenshot = false;
			int width = 0;
			int height = 0;
			NudeNetDetection[]? nsfwDetections = null;
			int allDetectionCount = 0;
			(NudeNetDetection[] Detections, ScanRegionInfo RegionInfo, int Bytes, long SlotEncodeMs, long SlotDetectMs)[]? results = null;

			try {
				var captureSw = Stopwatch.StartNew();
				using var screenshot = _screenCaptureService.CapturePrimaryScreen();
				captureSw.Stop();
				captureMs = captureSw.ElapsedMilliseconds;

				if(screenshot != null) {
					hadScreenshot = true;
					width = screenshot.Width;
					height = screenshot.Height;
					// Use hxh tiles (height-by-height) and let the backend downscale to its inference size.
					int scanSize = Math.Min(height, width);
					int maxX = Math.Max(0, width - scanSize);
					int midY = Math.Max(0, (height - scanSize) / 2);

					var scanRegions = new[] {
						new ScanRegionInfo(0, midY, "left"),
						new ScanRegionInfo(maxX / 2, midY, "center"),
						new ScanRegionInfo(maxX, midY, "right"),
					};

					var encodeSw = Stopwatch.StartNew();
					var detectSw = Stopwatch.StartNew();
					var regionTasks = new List<Task<(NudeNetDetection[] Detections, ScanRegionInfo RegionInfo, int Bytes, long SlotEncodeMs, long SlotDetectMs)>>();

					foreach (var region in scanRegions) {
						regionTasks.Add(Task.Run(async () => {
							var slotEncodeSw = Stopwatch.StartNew();
							byte[] imageBytes;
							using (var regionBitmap = new SKBitmap(InferenceSize, InferenceSize)) {
								using (var canvas = new SKCanvas(regionBitmap)) {
									var source = new SKRect(region.OffsetX, region.OffsetY, region.OffsetX + scanSize, region.OffsetY + scanSize);
									var dest = new SKRect(0, 0, InferenceSize, InferenceSize);
									canvas.DrawBitmap(screenshot, source, dest);
								}
								imageBytes = EncodeForTransport(regionBitmap);
							}
							slotEncodeSw.Stop();

							NudeNetDetection[] detections;
							var slotDetectSw = Stopwatch.StartNew();
							try {
								detections = await _nudeNetClient.DetectAsync(imageBytes, $"screen-{region.Name}.jpg", 0).ConfigureAwait(false);
							}
							catch (Exception ex) {
								AppLogger.Error($"DetectionLoopService detect error for {region.Name}", ex);
								detections = Array.Empty<NudeNetDetection>();
							}
							slotDetectSw.Stop();

							return (
								Detections: detections,
								RegionInfo: region,
								Bytes: imageBytes.Length,
								SlotEncodeMs: slotEncodeSw.ElapsedMilliseconds,
								SlotDetectMs: slotDetectSw.ElapsedMilliseconds
							);
						}));
					}

					results = await Task.WhenAll(regionTasks).ConfigureAwait(false);
					detectSw.Stop();
					encodeSw.Stop();
					encodeMs = results.Sum(x => x.SlotEncodeMs);
					encodedBytes = results.Sum(x => x.Bytes);
					detectMs = detectSw.ElapsedMilliseconds;

					// Consolidate and adjust coordinates.
					// Detection boxes are in InferenceSize×InferenceSize space; scale back to screen pixels first.
					double tileScale = (double)scanSize / InferenceSize;
					var allDetections = new List<NudeNetDetection>();
					foreach (var tileResult in results) {
						foreach (var detection in tileResult.Detections) {
							allDetections.Add(new NudeNetDetection(
								detection.Class,
								detection.Score,
								(int)Math.Round(detection.X * tileScale) + tileResult.RegionInfo.OffsetX,
								(int)Math.Round(detection.Y * tileScale) + tileResult.RegionInfo.OffsetY,
								(int)Math.Round(detection.Width * tileScale),
								(int)Math.Round(detection.Height * tileScale)
							));
						}
					}

					allDetectionCount = allDetections.Count;
					nsfwDetections = allDetections.Where(d => NsfwClassifier.IsNsfwDetection(d.Class, d.Score)).ToArray();

					//AppLogger.Info($"DetectionLoopService detections: {string.Join(", ", allDetections.Select(d => $"{d.Class}:{d.Score:F2}({d.X},{d.Y},{d.Width}x{d.Height})" ))}");

					if(nsfwDetections.Length > 0) {
						//AppLogger.Info($"DetectionLoopService.RunLoopAsync NSFW detected {nsfwDetections.Length} regions from 1 detection pass");
						NsfwDetected?.Invoke(cycleNumber, nsfwDetections);
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
				if (cycleNumber > 1) {
					_totalCycleMs += cycleSw.ElapsedMilliseconds;
					_measuredCycleCount++;
				}
				var slotBreakdown = results == null
					? "n/a"
					: string.Join(" | ", results.Select(r =>
						$"{r.RegionInfo.Name}: encode={r.SlotEncodeMs}ms detect={r.SlotDetectMs}ms detections={r.Detections.Length}"));
				AppLogger.Info(
					$"Benchmark cycle={cycleNumber} total={cycleSw.ElapsedMilliseconds}ms capture={captureMs}ms wallEncode={encodeMs}ms totalDetect={detectMs}ms screenshot={(hadScreenshot ? "yes" : "no")} size={width}x{height} bytes={encodedBytes} format=jpeg quality={TransportImageQuality} nsfw={(nsfwDetections?.Length ?? 0)}/{allDetectionCount} slots=[{slotBreakdown}]"
				);
				CycleCompleted?.Invoke(cycleNumber);
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