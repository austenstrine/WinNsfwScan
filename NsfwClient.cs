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

public class NsfwClient : IDisposable {
	private enum ServerKind {
		Erax,
		NudeNet,
	}

	private sealed record ServerConfig(string Folder, string Model, int Resolution, ServerKind Kind);

	private sealed class ServerInstance {
		public required Process Process { get; init; }
		public required int Port { get; init; }
		public required ServerKind Kind { get; init; }
		public required string Label { get; init; }
	}

	// Sequential pool order is intentional: 2 ERAx first, then 3 NudeNet.
	private static readonly ServerConfig[] ServerConfigs = {
		new("server", "erax_nsfw_yolo11n.onnx", 640, ServerKind.Erax),
		new("server", "erax_nsfw_yolo11n.onnx", 640, ServerKind.Erax),
		new("nn-server", "320n.onnx", 320, ServerKind.NudeNet),
		new("nn-server", "320n.onnx", 320, ServerKind.NudeNet),
		new("nn-server", "320n.onnx", 320, ServerKind.NudeNet),
	};

	private readonly List<ServerInstance> _servers = new();
	private readonly HttpClient _httpClient;
	private bool _disposed = false;

	public int ServerCount => _servers.Count;
	public int EraxServerCount => _servers.Count(s => s.Kind == ServerKind.Erax);
	public int NudeNetServerCount => _servers.Count(s => s.Kind == ServerKind.NudeNet);
	public bool IsNudeNetServer(int serverIndex) => _servers[serverIndex].Kind == ServerKind.NudeNet;

	public NsfwClient() {
		//AppLogger.Info("NudeNetClient.ctor entered");
		// Spawn all servers up-front so they initialise in parallel.
		var spawnedProcesses = new (Process Process, ServerKind Kind, string Label)[ServerConfigs.Length];
		for (int i = 0; i < ServerConfigs.Length; i++) {
			var (folder, model, resolution, kind) = ServerConfigs[i];
			string configPath = Path.Combine(AppContext.BaseDirectory, folder, "backend.json");
			string json = File.ReadAllText(configPath);
			using var doc = JsonDocument.Parse(json);
			string configuredServerExecutable = doc.RootElement.GetProperty("ServerExecutable").GetString()!;
			string configDirectory = Path.GetDirectoryName(configPath)!;
			string serverExecutable = Path.GetFullPath(Path.Combine(configDirectory, configuredServerExecutable));
			string label = $"{kind}-{i + 1}";

			var process = new Process {
				StartInfo = new ProcessStartInfo {
					FileName = serverExecutable,
					Arguments = $"--model {model} --resolution {resolution}",
					WorkingDirectory = configDirectory,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};
			process.Start();
			spawnedProcesses[i] = (process, kind, label);
			//AppLogger.Info($"NudeNetClient.ctor backend process {i+1} started ({model} @ {resolution})");
		}

		// Read each server's port announcement concurrently (each ReadLine blocks until ready).
		var portResults = new int[ServerConfigs.Length];
		var readTasks = spawnedProcesses.Select((spawned, i) => Task.Run(() => {
			string? line = spawned.Process.StandardOutput.ReadLine();
			if (line != null && line.StartsWith("PORT:")) {
				portResults[i] = int.Parse(line.Split(':')[1]);
				//AppLogger.Info($"NudeNetClient.ctor backend {i+1} announced port {portResults[i]}");
			}
			else {
				string stderr = spawned.Process.StandardError.ReadToEnd();
				AppLogger.Error($"NudeNetClient.ctor failed to read backend {i+1} port. FirstLine='{line ?? "<null>"}', stderr='{stderr}'");
				spawned.Process.Kill(entireProcessTree: true);
				spawned.Process.Dispose();
				throw new Exception($"Failed to read port from backend server {i+1}");
			}
		})).ToArray();

		Task.WhenAll(readTasks).GetAwaiter().GetResult();

		for (int i = 0; i < ServerConfigs.Length; i++) {
			_servers.Add(new ServerInstance {
				Process = spawnedProcesses[i].Process,
				Port = portResults[i],
				Kind = spawnedProcesses[i].Kind,
				Label = spawnedProcesses[i].Label,
			});
		}

		_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		Console.CancelKeyPress += OnCancelKeyPress;

		//AppLogger.Info($"NudeNetClient.ctor completed: {ServerConfigs.Length} servers on ports: {string.Join(", ", _servers.Select(s => s.Port))}");
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
	/// Detect NSFW content for a tile payload. ERAx expects raw BGRA; NudeNet can accept encoded image bytes.
	/// </summary>
	public async Task<NsfwDetection[]> DetectAsync(byte[] payloadBytes, string fileName, int serverIndex, string contentType = "application/octet-stream") {
		var server = _servers[serverIndex];
		string source = server.Kind == ServerKind.Erax ? "erax" : "nudenet";

		using var content = new ByteArrayContent(payloadBytes);
		content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

		var requestSw = Stopwatch.StartNew();
		var response = await _httpClient.PostAsync($"http://127.0.0.1:{server.Port}/detect_raw", content);
		requestSw.Stop();
		//AppLogger.Info($"NudeNetClient.DetectAsync http={requestSw.ElapsedMilliseconds}ms status={(int)response.StatusCode} server={serverIndex}");

		var readSw = Stopwatch.StartNew();
		var json = await response.Content.ReadAsStringAsync();
		readSw.Stop();

		if (!response.IsSuccessStatusCode) {
			AppLogger.Error($"NsfwClient.DetectAsync HTTP {(int)response.StatusCode} from server={serverIndex} source={source} file={fileName}: {json}");
			AppLogger.ModelError(source, $"Detect HTTP {(int)response.StatusCode} server={serverIndex} label={server.Label} file={fileName} requestMs={requestSw.ElapsedMilliseconds} readMs={readSw.ElapsedMilliseconds}");
			return Array.Empty<NsfwDetection>();
		}

		var parseSw = Stopwatch.StartNew();
		using var doc = JsonDocument.Parse(json);
		var detectionsEl = doc.RootElement.GetProperty("detections");

		var result = new List<NsfwDetection>();
		var nsfwRawDetections = new List<string>();
		foreach(var detection in detectionsEl.EnumerateArray()) {
			string className = "unknown";
			if(detection.TryGetProperty("class", out var classEl)) {
				className = classEl.GetString() ?? "unknown";
			}
			else if(detection.TryGetProperty("label", out var labelEl)) {
				className = labelEl.GetString() ?? "unknown";
			}

			float score = 0f;
			if(detection.TryGetProperty("score", out var scoreEl)) {
				score = (float)scoreEl.GetDouble();
			}
			else if(detection.TryGetProperty("confidence", out var confidenceEl)) {
				score = (float)confidenceEl.GetDouble();
			}

			var box = detection.GetProperty("box");
			int x = (int)box[0].GetDouble();
			int y = (int)box[1].GetDouble();
			int w = (int)box[2].GetDouble();
			int h = (int)box[3].GetDouble();
			result.Add(new NsfwDetection(className, score, x, y, w, h, source));

			if (NsfwClassifier.IsNsfwDetection(source, className, score)) {
				nsfwRawDetections.Add(detection.GetRawText());
			}
		}

		parseSw.Stop();
		AppLogger.ModelInfo(source,
			$"Perf server={serverIndex} label={server.Label} file={fileName} payloadBytes={payloadBytes.Length} contentType={contentType} requestMs={requestSw.ElapsedMilliseconds} readMs={readSw.ElapsedMilliseconds} parseMs={parseSw.ElapsedMilliseconds} totalMs={requestSw.ElapsedMilliseconds + readSw.ElapsedMilliseconds + parseSw.ElapsedMilliseconds} detections={result.Count} nsfw={nsfwRawDetections.Count}");

		if (nsfwRawDetections.Count > 0) {
			string nsfwJson = "[" + string.Join(",", nsfwRawDetections) + "]";
			AppLogger.Info($"NSFW positives server={serverIndex} source={source} label={server.Label} file={fileName} detections={nsfwJson}");
			AppLogger.ModelInfo(source, $"NSFW positives server={serverIndex} label={server.Label} file={fileName} detections={nsfwJson}");
		}

		//AppLogger.Info($"NudeNetClient.DetectAsync parse={parseSw.ElapsedMilliseconds}ms total={result.Count} explicit={result.Count(d => NsfwClassifier.IsNsfwClass(d.Class))}");

		return result.ToArray();
	}

	public void Dispose() {
		//AppLogger.Info("NudeNetClient.Dispose entered");
		if (_disposed) return;
		_disposed = true;

		// Ask each server to flush its logs and exit before we force-kill.
		for (int i = 0; i < _servers.Count; i++) {
			if (_servers[i].Process == null || _servers[i].Process.HasExited) continue;
			try {
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				_httpClient.PostAsync($"http://127.0.0.1:{_servers[i].Port}/shutdown", null, cts.Token).GetAwaiter().GetResult();
			}
			catch { }
		}

		try {
			foreach(var server in _servers) {
				var process = server.Process;
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

		//AppLogger.Info("NudeNetClient.Dispose completed - all server processes cleaned up");
	}
}