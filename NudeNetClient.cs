using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinNsfwScan;

public class NudeNetClient : IDisposable {
	// Server 0: full-screen detection with 640m model
	// Servers 1-4: quadrant detection with 320n model
	private static readonly (string Model, int Resolution)[] ServerConfigs = {
		("640m.onnx", 640),
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
		("320n.onnx", 320),
	};

	private readonly List<Process> _processes = new();
	private readonly List<int> _ports = new();
	private readonly HttpClient _httpClient;
	private int _currentServerIndex = 0;
	private bool _disposed = false;

	public NudeNetClient() {
		//AppLogger.Info("NudeNetClient.ctor entered");
		// Read config
		string configPath = Path.Combine(AppContext.BaseDirectory, "backend.json");
		//AppLogger.Info($"NudeNetClient.ctor reading config at {configPath}");
		string json = File.ReadAllText(configPath);
		using var doc = JsonDocument.Parse(json);
		string serverExecutable = doc.RootElement.GetProperty("ServerExecutable").GetString()!;
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

	public async Task<bool> IsNsfwAsync(string imagePath) {
		//AppLogger.Info("NudeNetClient.IsNsfwAsync(path) entered");
		var fileBytes = await File.ReadAllBytesAsync(imagePath);
		var detections = await DetectAsync(fileBytes, Path.GetFileName(imagePath));
		return detections.Any(d => NsfwClassifier.IsNsfwClass(d.Class));
	}

	/// <summary>
	/// Detect NSFW content in an image using a specific server (round-robin by default).
	/// </summary>
	public async Task<NudeNetDetection[]> DetectAsync(byte[] imageBytes, string fileName, int? serverIndex = null) {
		// Use round-robin if no specific server requested
		if (serverIndex == null) {
			serverIndex = _currentServerIndex;
			_currentServerIndex = (_currentServerIndex + 1) % ServerConfigs.Length;
		}

		int port = _ports[serverIndex.Value];

		//AppLogger.Info($"NudeNetClient.DetectAsync entered size={imageBytes.Length} server={serverIndex}");
		using var content = new MultipartFormDataContent();
		content.Add(new ByteArrayContent(imageBytes), "file", fileName);

		var requestSw = Stopwatch.StartNew();
		var response = await _httpClient.PostAsync($"http://127.0.0.1:{port}/detect", content);
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
		foreach(var detection in detectionsEl.EnumerateArray()) {
			string className = detection.GetProperty("class").GetString()!;
			float score = (float)detection.GetProperty("score").GetDouble();
			var box = detection.GetProperty("box");
			int x = (int)box[0].GetDouble();
			int y = (int)box[1].GetDouble();
			int w = (int)box[2].GetDouble();
			int h = (int)box[3].GetDouble();
			result.Add(new NudeNetDetection(className, score, x, y, w, h));
		}

		parseSw.Stop();
		//AppLogger.Info($"NudeNetClient.DetectAsync parse={parseSw.ElapsedMilliseconds}ms total={result.Count} explicit={result.Count(d => NsfwClassifier.IsNsfwClass(d.Class))}");

		return result.ToArray();
	}

	public void Dispose() {
		//AppLogger.Info("NudeNetClient.Dispose entered");
		if (_disposed) return;
		_disposed = true;

		try {
			foreach(var process in _processes) {
				if (process != null && !process.HasExited) {
					process.Kill(entireProcessTree: true);
					process.WaitForExit(2000);
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