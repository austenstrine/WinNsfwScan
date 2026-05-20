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
			(NudeNetDetection[] Detections, QuadrantInfo QuadInfo, int Bytes, long SlotEncodeMs, long SlotDetectMs)[]? results = null;

			try {
				var captureSw = Stopwatch.StartNew();
				using var screenshot = _screenCaptureService.CapturePrimaryScreen();
				captureSw.Stop();
				captureMs = captureSw.ElapsedMilliseconds;

				if(screenshot != null) {
					hadScreenshot = true;
					width = screenshot.Width;
					height = screenshot.Height;

					// Prepare full screen + 4 quadrants
					var quadrantInfos = new[] {
						new QuadrantInfo(0, 0, "full"),
						new QuadrantInfo(0, 0, "quad-tl"),
						new QuadrantInfo(width / 2, 0, "quad-tr"),
						new QuadrantInfo(0, height / 2, "quad-bl"),
						new QuadrantInfo(width / 2, height / 2, "quad-br"),
					};

					int quadWidth = width / 2;
					int quadHeight = height / 2;

					var encodeSw = Stopwatch.StartNew();

					// Encode all 5 images in parallel (full screen + 4 quadrants)
					var encodeAndDetectTasks = quadrantInfos
						.Select((info, idx) => Task.Run(async () => {
							byte[] imageBytes;
							string fileName;

							var slotEncodeSw = Stopwatch.StartNew();
							if (idx == 0) {
								// Full screen
								imageBytes = EncodeForTransport(screenshot);
								fileName = "screen-full.jpg";
							}
							else {
								// Quadrant - extract and encode
								using var quadBitmap = new SKBitmap(quadWidth, quadHeight);
								using (var canvas = new SKCanvas(quadBitmap)) {
									var source = new SKRect(info.OffsetX, info.OffsetY, info.OffsetX + quadWidth, info.OffsetY + quadHeight);
									var dest = new SKRect(0, 0, quadWidth, quadHeight);
									canvas.DrawBitmap(screenshot, source, dest);
								}
								imageBytes = EncodeForTransport(quadBitmap);
								fileName = $"screen-{info.Name}.jpg";
							}
							slotEncodeSw.Stop();

							// Immediately detect after encoding (each on its own thread)
							NudeNetDetection[] detections;
							var slotDetectSw = Stopwatch.StartNew();
							try {
								detections = await _nudeNetClient.DetectAsync(imageBytes, fileName, idx).ConfigureAwait(false);
							}
							catch (Exception ex) {
								AppLogger.Error($"DetectionLoopService parallel detect error for {info.Name} server {idx}", ex);
								detections = Array.Empty<NudeNetDetection>();
							}
							slotDetectSw.Stop();

							return (
								Detections: detections,
								QuadInfo: info,
								Bytes: imageBytes.Length,
								SlotEncodeMs: slotEncodeSw.ElapsedMilliseconds,
								SlotDetectMs: slotDetectSw.ElapsedMilliseconds
							);
						}))
						.ToList();

					results = await Task.WhenAll(encodeAndDetectTasks).ConfigureAwait(false);

					encodeSw.Stop();
					encodeMs = encodeSw.ElapsedMilliseconds;
					encodedBytes = results.Sum(x => x.Bytes);
					encodedFormat = "jpeg";
					detectMs = results.Max(x => x.SlotDetectMs);

					// Consolidate and adjust coordinates
					var allDetections = new List<NudeNetDetection>();
					foreach (var result in results) {
						foreach (var detection in result.Detections) {
							allDetections.Add(new NudeNetDetection(
								detection.Class,
								detection.Score,
								detection.X + result.QuadInfo.OffsetX,
								detection.Y + result.QuadInfo.OffsetY,
								detection.Width,
								detection.Height
							));
						}
					}

					allDetectionCount = allDetections.Count;
					nsfwDetections = allDetections.Where(d => NsfwClassifier.IsNsfwClass(d.Class)).ToArray();

					//AppLogger.Info($"DetectionLoopService detections: {string.Join(", ", allDetections.Select(d => $"{d.Class}:{d.Score:F2}({d.X},{d.Y},{d.Width}x{d.Height})" ))}");

					if(nsfwDetections.Length > 0) {
						//AppLogger.Info($"DetectionLoopService.RunLoopAsync NSFW detected {nsfwDetections.Length} regions from 5 detection passes");
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
				var slotBreakdown = results == null ? "n/a" :
					string.Join(" | ", results.Select((r, i) => {
						string model = i == 0 ? "640m" : "320n";
						return $"{r.QuadInfo.Name}({model}): encode={r.SlotEncodeMs}ms detect={r.SlotDetectMs}ms detections={r.Detections.Length}";
					}));
				AppLogger.Info(
					$"Benchmark cycle={cycleNumber} total={cycleSw.ElapsedMilliseconds}ms capture={captureMs}ms wallEncode={encodeMs}ms slowestDetect={detectMs}ms screenshot={(hadScreenshot ? "yes" : "no")} size={width}x{height} bytes={encodedBytes} format={encodedFormat} quality={TransportImageQuality} nsfw={(nsfwDetections?.Length ?? 0)}/{allDetectionCount} slots=[{slotBreakdown}]"
				);
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