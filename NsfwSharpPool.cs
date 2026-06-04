using System;
using System.Threading;
using System.Threading.Tasks;
using NsfwSharp;
using SkiaSharp;

namespace WinNsfwScan;

/// <summary>
/// Thread-safe pool of NsfwAnalyzer instances for in-process YOLO inference via YoloDotNet.
/// Each instance has its own semaphore; callers select an instance by index so dispatch
/// is identical to the HTTP server round-robin in NsfwClient.
/// </summary>
public sealed class NsfwSharpPool : IDisposable {
	private readonly NsfwAnalyzer[] _analyzers;
	private readonly SemaphoreSlim[] _slots;
	private bool _disposed;

	public int InstanceCount => _analyzers.Length;

	public NsfwSharpPool(string modelPath, int instanceCount) {
		_analyzers = new NsfwAnalyzer[instanceCount];
		_slots = new SemaphoreSlim[instanceCount];
		for(int i = 0; i < instanceCount; i++) {
			_analyzers[i] = new NsfwAnalyzer(modelPath);
			_slots[i] = new SemaphoreSlim(1, 1);
		}
	}

	/// <summary>
	/// Runs inference on the supplied tile bitmap using the specified pool instance.
	/// Returns detections in tile-local pixel coordinates (as produced by YoloDotNet).
	/// </summary>
	public async Task<NsfwDetection[]> AnalyzeAsync(SKBitmap tile, int instanceIndex) {
		await _slots[instanceIndex].WaitAsync().ConfigureAwait(false);
		NsfwAnalysis analysis;
		try {
			analysis = _analyzers[instanceIndex].GetNsfwAnalysis(tile);
		}
		finally {
			_slots[instanceIndex].Release();
		}

		var results = new NsfwDetection[analysis.Detections.Length];
		for(int i = 0; i < analysis.Detections.Length; i++) {
			var d = analysis.Detections[i];
			results[i] = new NsfwDetection(d.Name, (float)d.Confidence, d.X, d.Y, d.Width, d.Height, "nsfwsharp");
		}
		return results;
	}

	public void Dispose() {
		if(_disposed) return;
		_disposed = true;
		foreach(var analyzer in _analyzers)
			analyzer.Dispose();
		foreach(var slot in _slots)
			slot.Dispose();
	}
}
