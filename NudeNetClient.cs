using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinNsfwScan;

public class NudeNetClient : IDisposable {
	// One server process per tile so each has its own isolated DML device and can
	// run inference in parallel without GPU device contention.
	private static readonly (string Model, int Resolution)[] ServerConfigs = {
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
	};

	private readonly List<Process> _processes = new();
	private readonly List<int> _ports = new();
	private readonly HttpClient _httpClient;
	private bool _disposed = false;

	public int ServerCount => _ports.Count;

	public NudeNetClient() {
		//AppLogger.Info("NudeNetClient.ctor entered");
		// Read config
		string configPath = Path.Combine(AppContext.BaseDirectory, "server", "backend.json");
		//AppLogger.Info($"NudeNetClient.ctor reading config at {configPath}");
		string json = File.ReadAllText(configPath);
		using var doc = JsonDocument.Parse(json);
		// Resolve server executable relative to backend config location.
		string configuredServerExecutable = doc.RootElement.GetProperty("ServerExecutable").GetString()!;
		string configDirectory = Path.GetDirectoryName(configPath)!;
		string serverExecutable = Path.GetFullPath(Path.Combine(configDirectory, configuredServerExecutable));
		//AppLogger.Info($"NudeNetClient.ctor server executable={serverExecutable}");

		// Spawn servers with per-slot model configuration
		for (int i = 0; i < ServerConfigs.Length; i++) {
			var (model, resolution) = ServerConfigs[i];
			var process = new Process {
				StartInfo = new ProcessStartInfo {
					FileName = serverExecutable,
					Arguments = $"--model {model} --resolution {resolution}",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};

			process.Start();
			//AppLogger.Info($"NudeNetClient.ctor backend process {i+1} started ({model} @ {resolution})");

			string? line = process.StandardOutput.ReadLine();
			if (line != null && line.StartsWith("PORT:")) {
				int port = int.Parse(line.Split(':')[1]);
				_ports.Add(port);
				_processes.Add(process);
				//AppLogger.Info($"NudeNetClient.ctor backend {i+1} ({model} @ {resolution}) announced port {port}");
			}
			else {
				string stderr = process.StandardError.ReadToEnd();
				AppLogger.Error($"NudeNetClient.ctor failed to read backend {i+1} port. FirstLine='{line ?? "<null>"}', stderr='{stderr}'");
				process.Kill(entireProcessTree: true);
				process.Dispose();
				throw new Exception($"Failed to read port from backend server {i+1}");
			}
		}

		_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		Console.CancelKeyPress += OnCancelKeyPress;

		//AppLogger.Info($"NudeNetClient.ctor completed: {ServerConfigs.Length} servers on ports: {string.Join(", ", _ports)}");
	}

	private void OnProcessExit(object? sender, EventArgs e) {
		//AppLogger.Info("NudeNetClient.OnProcessExit entered");
		Dispose();
	}

	private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) {
		//AppLogger.Info("NudeNetClient.OnCancelKeyPress entered");
		Dispose();
	}

	/// <summary>
	/// Detect NSFW content in an image on the given server.
	/// Sends raw JPEG bytes as application/octet-stream to avoid multipart parsing overhead.
	/// </summary>
	public async Task<NudeNetDetection[]> DetectAsync(byte[] imageBytes, string fileName, int serverIndex) {

		int port = _ports[serverIndex];

		//AppLogger.Info($"NudeNetClient.DetectAsync entered size={imageBytes.Length} server={serverIndex}");
		using var content = new ByteArrayContent(imageBytes);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

		var requestSw = Stopwatch.StartNew();
		var response = await _httpClient.PostAsync($"http://127.0.0.1:{port}/detect_raw", content);
		requestSw.Stop();
		//AppLogger.Info($"NudeNetClient.DetectAsync http={requestSw.ElapsedMilliseconds}ms status={(int)response.StatusCode} server={serverIndex}");

		var readSw = Stopwatch.StartNew();
		var json = await response.Content.ReadAsStringAsync();
		readSw.Stop();
		//AppLogger.Info($"NudeNetClient.DetectAsync readBody={readSw.ElapsedMilliseconds}ms length={json.Length}");

		var parseSw = Stopwatch.StartNew();
		using var doc = JsonDocument.Parse(json);
		var detectionsEl = doc.RootElement.GetProperty("detections");

		var result = new List<NudeNetDetection>();
		var nsfwRawDetections = new List<string>();
		foreach(var detection in detectionsEl.EnumerateArray()) {
			string className = detection.GetProperty("class").GetString()!;
			float score = (float)detection.GetProperty("score").GetDouble();
			var box = detection.GetProperty("box");
			int x = (int)box[0].GetDouble();
			int y = (int)box[1].GetDouble();
			int w = (int)box[2].GetDouble();
			int h = (int)box[3].GetDouble();
			result.Add(new NudeNetDetection(className, score, x, y, w, h));

			if (NsfwClassifier.IsNsfwDetection(className, score)) {
				nsfwRawDetections.Add(detection.GetRawText());
			}
		}

		parseSw.Stop();

		if (nsfwRawDetections.Count > 0) {
			string nsfwJson = "[" + string.Join(",", nsfwRawDetections) + "]";
			AppLogger.Info($"NudeNet NSFW positives server={serverIndex} file={fileName} detections={nsfwJson}");
		}

		//AppLogger.Info($"NudeNetClient.DetectAsync parse={parseSw.ElapsedMilliseconds}ms total={result.Count} explicit={result.Count(d => NsfwClassifier.IsNsfwClass(d.Class))}");

		return result.ToArray();
	}

	public void Dispose() {
		//AppLogger.Info("NudeNetClient.Dispose entered");
		if (_disposed) return;
		_disposed = true;

		// Ask each server to flush its logs and exit before we force-kill.
		for (int i = 0; i < _processes.Count; i++) {
			if (_processes[i] == null || _processes[i].HasExited) continue;
			try {
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				_httpClient.PostAsync($"http://127.0.0.1:{_ports[i]}/shutdown", null, cts.Token).GetAwaiter().GetResult();
			}
			catch { }
		}

		try {
			foreach(var process in _processes) {
				if (process != null && !process.HasExited) {
					process.WaitForExit(3000);
					if (!process.HasExited)
						process.Kill(entireProcessTree: true);
				}
				process?.Dispose();
			}
		}
		catch (Exception ex) {
			AppLogger.Error("NudeNetClient.Dispose error killing processes", ex);
		}

		_httpClient?.Dispose();

		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		Console.CancelKeyPress -= OnCancelKeyPress;

		//AppLogger.Info("NudeNetClient.Dispose completed - all 5 server processes cleaned up");
	}
}