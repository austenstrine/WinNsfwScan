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
	private readonly NsfwClient _nsfwClient;
	private readonly NsfwSharpPool _nsfwSharpPool;
	private readonly TimeSpan _scanInterval;
	public event Action<long, NsfwDetection[]>? NsfwDetected;
	public event Action<long>? CycleCompleted;

	private record ScanRegionInfo(int OffsetX, int OffsetY, int Width, int Height, string Name);

	private CancellationTokenSource? _cts;
	private Task? _loopTask;
	private bool _disposed;
	private static readonly (int Columns, int Rows)[] GridCyclePatterns = [
		(3, 2),
		(5, 6),
	];

	private long _totalCycleMs;
	private long _measuredCycleCount;

	/// <summary>Average cycle duration in milliseconds, excluding the first warm-up cycle. Null if fewer than two cycles have completed.</summary>
	public double? AverageCycleMs => _measuredCycleCount > 0
		? (double)_totalCycleMs / _measuredCycleCount
		: null;

	public DetectionLoopService(ScreenCaptureService screenCaptureService, NsfwClient nudeNetClient, NsfwSharpPool nsfwSharpPool, TimeSpan? scanInterval = null) {
		//AppLogger.Info("DetectionLoopService.ctor entered");
		_screenCaptureService = screenCaptureService;
		_nsfwClient = nudeNetClient;
		_nsfwSharpPool = nsfwSharpPool;
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
			string captureBackend = "none";
			long encodeMs = 0;
			long detectMs = 0;
			int encodedBytes = 0;
			bool hadScreenshot = false;
			int width = 0;
			int height = 0;
			NsfwDetection[]? nsfwDetections = null;
			int allDetectionCount = 0;
			int allEraxDetectionCount = 0;
			int allNudeNetDetectionCount = 0;
			int allNsfwSharpDetectionCount = 0;
			int nsfwEraxDetectionCount = 0;
			int nsfwNudeNetDetectionCount = 0;
			int nsfwNsfwSharpDetectionCount = 0;
			(NsfwDetection[] Detections, ScanRegionInfo RegionInfo, int Bytes, long SlotExtractMs, long SlotDetectMs)[]? results = null;

			try {
				var captureSw = Stopwatch.StartNew();
				using var screenshot = _screenCaptureService.CapturePrimaryScreen(out captureBackend);
				captureSw.Stop();
				captureMs = captureSw.ElapsedMilliseconds;

				if(screenshot != null) {
					hadScreenshot = true;
					width = screenshot.Width;
					height = screenshot.Height;
					int cyclePatternIndex = (int)((cycleNumber - 1) % GridCyclePatterns.Length);
					var grid = GridCyclePatterns[cyclePatternIndex];
					int tileColumns = grid.Columns;
					int tileRows = grid.Rows;
					var scanRegions = new List<ScanRegionInfo>(tileColumns * tileRows);
					for(int row = 0; row < tileRows; row++) {
						int y0 = (height * row) / tileRows;
						int y1 = (height * (row + 1)) / tileRows;
						int tileH = Math.Max(1, y1 - y0);

						for(int col = 0; col < tileColumns; col++) {
							int x0 = (width * col) / tileColumns;
							int x1 = (width * (col + 1)) / tileColumns;
							int tileW = Math.Max(1, x1 - x0);
							scanRegions.Add(new ScanRegionInfo(x0, y0, tileW, tileH, $"r{row}c{col}"));
						}
					}

					var detectSw = Stopwatch.StartNew();
					var regionTasks = new List<Task<(NsfwDetection[] Detections, ScanRegionInfo RegionInfo, int Bytes, long SlotExtractMs, long SlotDetectMs)>>();
					// One semaphore per HTTP server — each server handles only one inference at a time.
					int httpCount = _nsfwClient.ServerCount;
					int totalSlots = httpCount + _nsfwSharpPool.InstanceCount;
					var serverSlots = Enumerable.Range(0, httpCount)
						.Select(_ => new SemaphoreSlim(1, 1))
						.ToArray();

					for (int tileIndex = 0; tileIndex < scanRegions.Count; tileIndex++) {
						var region = scanRegions[tileIndex];
						int slotIndex = tileIndex % totalSlots;
						bool isNsfwSharp = slotIndex >= httpCount;
						int serverIndex = slotIndex;
						int nsfwSharpInstanceIndex = slotIndex - httpCount;
						SemaphoreSlim? serverSlot = isNsfwSharp ? null : serverSlots[slotIndex];
						regionTasks.Add(Task.Run(async () => {
							var slotExtractSw = Stopwatch.StartNew();
							byte[] payloadBytes;
							string contentType;
							double preprocessScale;
							int padLeft;
							int padTop;

							if(isNsfwSharp) {
								// NsfwSharp path: extract raw tile SKBitmap; YoloDotNet preprocesses internally.
								padLeft = 0;
								padTop = 0;
								preprocessScale = 1.0;
								payloadBytes = Array.Empty<byte>();
								contentType = "nsfwsharp";

								using var tileBitmap = new SKBitmap(region.Width, region.Height);
								using(var canvas = new SKCanvas(tileBitmap)) {
									canvas.Clear(SKColors.Black);
									canvas.DrawBitmap(screenshot,
										new SKRect(region.OffsetX, region.OffsetY, region.OffsetX + region.Width, region.OffsetY + region.Height),
										new SKRect(0, 0, region.Width, region.Height));
								}
								slotExtractSw.Stop();

								NsfwDetection[] nsfwSharpRaw;
							var nsfwSharpDetectSw = Stopwatch.StartNew();
							try {
								nsfwSharpRaw = await _nsfwSharpPool.AnalyzeAsync(tileBitmap, nsfwSharpInstanceIndex).ConfigureAwait(false);
							}
							catch(Exception ex) {
								AppLogger.Error($"DetectionLoopService NsfwSharp error for {region.Name}", ex);
								AppLogger.ModelError("nsfwsharp", $"Tile error region={region.Name} instance={nsfwSharpInstanceIndex}", ex);
								nsfwSharpRaw = Array.Empty<NsfwDetection>();
							}
							nsfwSharpDetectSw.Stop();
							int nsfwSharpPositives = nsfwSharpRaw.Count(d => NsfwClassifier.IsNsfwDetection(d.Source, d.Class, d.Score));
							AppLogger.ModelInfo("nsfwsharp",
								$"Perf region={region.Name} instance={nsfwSharpInstanceIndex} extractMs={slotExtractSw.ElapsedMilliseconds} detectMs={nsfwSharpDetectSw.ElapsedMilliseconds} totalDetections={nsfwSharpRaw.Length} nsfwDetections={nsfwSharpPositives} tile={region.Width}x{region.Height}");

							return (
								Detections: nsfwSharpRaw,
								RegionInfo: region,
								Bytes: 0,
								SlotExtractMs: slotExtractSw.ElapsedMilliseconds,
								SlotDetectMs: nsfwSharpDetectSw.ElapsedMilliseconds
								);
							}
							else if(_nsfwClient.IsNudeNetServer(serverIndex)) {
								// NudeNet path: preserve tile dimensions and pad to square only.
								// NudeNet performs its own internal resize to model resolution.
								int squareSize = Math.Max(region.Width, region.Height);
								padLeft = (squareSize - region.Width) / 2;
								padTop = (squareSize - region.Height) / 2;
								preprocessScale = 1.0;

								using var squareBitmap = new SKBitmap(squareSize, squareSize);
								using(var canvas = new SKCanvas(squareBitmap)) {
									canvas.Clear(new SKColor(114, 114, 114));
									canvas.DrawBitmap(screenshot,
										new SKRect(region.OffsetX, region.OffsetY, region.OffsetX + region.Width, region.OffsetY + region.Height),
										new SKRect(padLeft, padTop, padLeft + region.Width, padTop + region.Height));
								}

								using var image = SKImage.FromBitmap(squareBitmap);
								using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
								payloadBytes = encoded.ToArray();
								contentType = "image/png";
							}
							else {
								// ERAx path: C# letterboxes to 640x640 raw BGRA before inference.
								const int modelSize = 640;
								preprocessScale = Math.Min((double)modelSize / region.Width, (double)modelSize / region.Height);
								int scaledW = (int)(region.Width * preprocessScale);
								int scaledH = (int)(region.Height * preprocessScale);
								padLeft = (modelSize - scaledW) / 2;
								padTop = (modelSize - scaledH) / 2;

								using var lbBitmap = new SKBitmap(modelSize, modelSize);
								using(var canvas = new SKCanvas(lbBitmap)) {
									canvas.Clear(new SKColor(114, 114, 114));
									canvas.DrawBitmap(screenshot,
										new SKRect(region.OffsetX, region.OffsetY, region.OffsetX + region.Width, region.OffsetY + region.Height),
										new SKRect(padLeft, padTop, padLeft + scaledW, padTop + scaledH));
								}

								payloadBytes = lbBitmap.Bytes;
								contentType = "application/octet-stream";
							}
							slotExtractSw.Stop();

							NsfwDetection[] rawDetections;
							var slotDetectSw = Stopwatch.StartNew();
							try {
									await serverSlot!.WaitAsync().ConfigureAwait(false);
								try {
									rawDetections = await _nsfwClient.DetectAsync(payloadBytes, $"screen-{region.Name}", serverIndex, contentType).ConfigureAwait(false);
								} finally {
									serverSlot.Release();
								}
							}
							catch (Exception ex) {
								AppLogger.Error($"DetectionLoopService detect error for {region.Name}", ex);
								rawDetections = Array.Empty<NsfwDetection>();
							}
							slotDetectSw.Stop();

							// Un-project boxes from padded model-space back to tile-local coordinates.
							var detections = rawDetections.Select(d => new NsfwDetection(
								d.Class, d.Score,
								(int)Math.Round(Math.Max(0.0, d.X - padLeft) / preprocessScale),
								(int)Math.Round(Math.Max(0.0, d.Y - padTop) / preprocessScale),
								(int)Math.Round(d.Width / preprocessScale),
								(int)Math.Round(d.Height / preprocessScale),
								d.Source
							)).ToArray();

							return (
								Detections: detections,
								RegionInfo: region,
								Bytes: payloadBytes.Length,
								SlotExtractMs: slotExtractSw.ElapsedMilliseconds,
								SlotDetectMs: slotDetectSw.ElapsedMilliseconds						);
					}));
				}

				results = await Task.WhenAll(regionTasks).ConfigureAwait(false);
				detectSw.Stop();					encodeMs = results.Sum(x => x.SlotExtractMs);
					encodedBytes = results.Sum(x => x.Bytes);
					detectMs = detectSw.ElapsedMilliseconds;

					// Consolidate detections, mapping tile-local coordinates → screen coordinates.
					var allDetections = new List<NsfwDetection>();
					foreach (var tileResult in results) {
						foreach (var detection in tileResult.Detections) {
							allDetections.Add(new NsfwDetection(
								detection.Class,
								detection.Score,
								detection.X + tileResult.RegionInfo.OffsetX,
								detection.Y + tileResult.RegionInfo.OffsetY,
								detection.Width,
								detection.Height,
								detection.Source
							));
						}
					}

					allDetectionCount = allDetections.Count;
					allEraxDetectionCount = allDetections.Count(d => d.Source.Equals("erax", StringComparison.OrdinalIgnoreCase));
					allNudeNetDetectionCount = allDetections.Count(d => d.Source.Equals("nudenet", StringComparison.OrdinalIgnoreCase));
					allNsfwSharpDetectionCount = allDetections.Count(d => d.Source.Equals("nsfwsharp", StringComparison.OrdinalIgnoreCase));
					nsfwDetections = allDetections.Where(d => NsfwClassifier.IsNsfwDetection(d.Source, d.Class, d.Score)).ToArray();
					nsfwEraxDetectionCount = nsfwDetections.Count(d => d.Source.Equals("erax", StringComparison.OrdinalIgnoreCase));
					nsfwNudeNetDetectionCount = nsfwDetections.Count(d => d.Source.Equals("nudenet", StringComparison.OrdinalIgnoreCase));
					nsfwNsfwSharpDetectionCount = nsfwDetections.Count(d => d.Source.Equals("nsfwsharp", StringComparison.OrdinalIgnoreCase));

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

				AppLogger.ModelInfo("erax",
					$"CycleSummary cycle={cycleNumber} totalMs={cycleSw.ElapsedMilliseconds} detectMs={detectMs} allDetections={allEraxDetectionCount} nsfwDetections={nsfwEraxDetectionCount}");
				AppLogger.ModelInfo("nudenet",
					$"CycleSummary cycle={cycleNumber} totalMs={cycleSw.ElapsedMilliseconds} detectMs={detectMs} allDetections={allNudeNetDetectionCount} nsfwDetections={nsfwNudeNetDetectionCount}");
				AppLogger.ModelInfo("nsfwsharp",
					$"CycleSummary cycle={cycleNumber} totalMs={cycleSw.ElapsedMilliseconds} detectMs={detectMs} allDetections={allNsfwSharpDetectionCount} nsfwDetections={nsfwNsfwSharpDetectionCount}");

				var slotBreakdown = results == null
					? "n/a"
					: string.Join(" | ", results.Select(r =>
						$"{r.RegionInfo.Name}: extract={r.SlotExtractMs}ms detect={r.SlotDetectMs}ms detections={r.Detections.Length}"));
				AppLogger.Info(
					$"Benchmark cycle={cycleNumber} total={cycleSw.ElapsedMilliseconds}ms capture={captureMs}ms captureBackend={captureBackend} wallExtract={encodeMs}ms totalDetect={detectMs}ms screenshot={(hadScreenshot ? "yes" : "no")} size={width}x{height} bytes={encodedBytes} format=mixed nsfw={(nsfwDetections?.Length ?? 0)}/{allDetectionCount} nsfwByModel=erax:{nsfwEraxDetectionCount},nudenet:{nsfwNudeNetDetectionCount},nsfwsharp:{nsfwNsfwSharpDetectionCount} allByModel=erax:{allEraxDetectionCount},nudenet:{allNudeNetDetectionCount},nsfwsharp:{allNsfwSharpDetectionCount} slots=[{slotBreakdown}]"
				);
				CycleCompleted?.Invoke(cycleNumber);
			}

			await Task.Delay(_scanInterval, cancellationToken).ConfigureAwait(false);
		}

		//AppLogger.Info("DetectionLoopService.RunLoopAsync stopped");
	}

	public void Dispose() {
		//AppLogger.Info("DetectionLoopService.Dispose entered");
		if(_disposed)
			return;

		_disposed = true;

		StopAsync().GetAwaiter().GetResult();
		_nsfwClient.Dispose();
		_nsfwSharpPool.Dispose();
		_screenCaptureService.Dispose();
		//AppLogger.Info("DetectionLoopService.Dispose completed");
	}
}