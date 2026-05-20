using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinNsfwScan;

public class NudeNetClient : IDisposable {
	private static readonly HashSet<string> ExplicitClasses = new() {
		"FEMALE_GENITALIA_EXPOSED",
		"FEMALE_GENITALIA_COVERED",
		"MALE_GENITALIA_EXPOSED",
		"ANUS_EXPOSED",
		"ANUS_COVERED",
		"FEMALE_BREAST_EXPOSED",
		"FEMALE_BREAST_COVERED",
		"MALE_BREAST_EXPOSED",
		"BUTTOCKS_EXPOSED",
		"BUTTOCKS_COVERED"
	};
	private readonly Process _process;
	private readonly HttpClient _httpClient;
	private readonly int _port;
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

		_process = new Process {
			StartInfo = new ProcessStartInfo {
				FileName = serverExecutable,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			}
		};

		_process.Start();
		//AppLogger.Info("NudeNetClient.ctor backend process started");

		string? line = _process.StandardOutput.ReadLine();
		if (line != null && line.StartsWith("PORT:")) {
			_port = int.Parse(line.Split(':')[1]);
			//AppLogger.Info($"NudeNetClient.ctor backend announced port {_port}");
		}
		else {
			string stderr = _process.StandardError.ReadToEnd();
			AppLogger.Error($"NudeNetClient.ctor failed to read backend port. FirstLine='{line ?? "<null>"}', stderr='{stderr}'");
			throw new Exception("Failed to read port from backend");
		}

		_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		Console.CancelKeyPress += OnCancelKeyPress;

		AppLogger.Info($"NudeNetClient.ctor completed on port {_port}");
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
		return detections.Any(d => ExplicitClasses.Contains(d.Class));
	}

	public async Task<NudeNetDetection[]> DetectAsync(byte[] imageBytes, string fileName) {
		//AppLogger.Info($"NudeNetClient.DetectAsync entered size={imageBytes.Length}");
		using var content = new MultipartFormDataContent();
		content.Add(new ByteArrayContent(imageBytes), "file", fileName);

		var requestSw = Stopwatch.StartNew();
		var response = await _httpClient.PostAsync($"http://127.0.0.1:{_port}/detect", content);
		requestSw.Stop();
		AppLogger.Info($"NudeNetClient.DetectAsync http={requestSw.ElapsedMilliseconds}ms status={(int)response.StatusCode}");

		var readSw = Stopwatch.StartNew();
		var json = await response.Content.ReadAsStringAsync();
		readSw.Stop();
		AppLogger.Info($"NudeNetClient.DetectAsync readBody={readSw.ElapsedMilliseconds}ms length={json.Length}");

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
		AppLogger.Info($"NudeNetClient.DetectAsync parse={parseSw.ElapsedMilliseconds}ms total={result.Count} explicit={result.Count(d => ExplicitClasses.Contains(d.Class))}");

		return result.ToArray();
	}

	public void Dispose() {
		//AppLogger.Info("NudeNetClient.Dispose entered");
		if (_disposed) return;
		_disposed = true;

		try {
			if (_process != null && !_process.HasExited) {
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit(2000);
			}
		}
		catch { }

		_process?.Dispose();
		_httpClient?.Dispose();

		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		Console.CancelKeyPress -= OnCancelKeyPress;

		//AppLogger.Info("NudeNetClient.Dispose completed");
	}
}