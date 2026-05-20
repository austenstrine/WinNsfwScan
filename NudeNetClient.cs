using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class NudeNetClient : IDisposable {
	private readonly Process _process;
	private readonly HttpClient _httpClient;
	private readonly int _port;
	private bool _disposed = false;

	public NudeNetClient() {
		WinNsfwScan.AppLogger.Info("NudeNetClient.ctor entered");
		// Read config
		string configPath = Path.Combine(AppContext.BaseDirectory, "backend.json");
		WinNsfwScan.AppLogger.Info($"NudeNetClient.ctor reading config at {configPath}");
		string json = File.ReadAllText(configPath);
		using var doc = JsonDocument.Parse(json);
		string serverExecutable = doc.RootElement.GetProperty("ServerExecutable").GetString()!;
		WinNsfwScan.AppLogger.Info($"NudeNetClient.ctor server executable={serverExecutable}");

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
		WinNsfwScan.AppLogger.Info("NudeNetClient.ctor backend process started");

		string? line = _process.StandardOutput.ReadLine();
		if (line != null && line.StartsWith("PORT:")) {
			_port = int.Parse(line.Split(':')[1]);
			WinNsfwScan.AppLogger.Info($"NudeNetClient.ctor backend announced port {_port}");
		}
		else {
			string stderr = _process.StandardError.ReadToEnd();
			WinNsfwScan.AppLogger.Error($"NudeNetClient.ctor failed to read backend port. FirstLine='{line ?? "<null>"}', stderr='{stderr}'");
			throw new Exception("Failed to read port from backend");
		}

		_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		Console.CancelKeyPress += OnCancelKeyPress;

		WinNsfwScan.AppLogger.Info($"NudeNetClient.ctor completed on port {_port}");
	}

	private void OnProcessExit(object? sender, EventArgs e) {
		WinNsfwScan.AppLogger.Info("NudeNetClient.OnProcessExit entered");
		Dispose();
	}

	private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) {
		WinNsfwScan.AppLogger.Info("NudeNetClient.OnCancelKeyPress entered");
		Dispose();
	}

	public async Task<bool> IsNsfwAsync(string imagePath) {
		WinNsfwScan.AppLogger.Info("NudeNetClient.IsNsfwAsync(path) entered");
		var fileBytes = await File.ReadAllBytesAsync(imagePath);
		return await IsNsfwAsync(fileBytes, Path.GetFileName(imagePath));
	}

	public async Task<bool> IsNsfwAsync(byte[] imageBytes, string fileName) {
		WinNsfwScan.AppLogger.Info($"NudeNetClient.IsNsfwAsync(bytes) entered size={imageBytes.Length}");
		using var content = new MultipartFormDataContent();
		content.Add(new ByteArrayContent(imageBytes), "file", fileName);

		var sw = Stopwatch.StartNew();
		var response = await _httpClient.PostAsync($"http://127.0.0.1:{_port}/detect", content);
		sw.Stop();
		WinNsfwScan.AppLogger.Info($"NudeNetClient.IsNsfwAsync(bytes) round-trip={sw.ElapsedMilliseconds}ms status={(int)response.StatusCode}");
		
		var json = await response.Content.ReadAsStringAsync();
		WinNsfwScan.AppLogger.Info($"NudeNetClient.IsNsfwAsync(bytes) response={json}");

		using var doc = JsonDocument.Parse(json);
		var detections = doc.RootElement.GetProperty("detections");

		var explicitClasses = new HashSet<string>
		{
			"FEMALE_GENITALIA_EXPOSED",
			"MALE_GENITALIA_EXPOSED",
			"ANUS_EXPOSED",
			"FEMALE_BREAST_EXPOSED",
			"BUTTOCKS_EXPOSED",
			"MALE_BREAST_EXPOSED"
		};

		bool isNsfw = false;
		var triggeredClasses = new List<string>();

		foreach (var detection in detections.EnumerateArray())
		{
			string className = detection.GetProperty("class").GetString()!;
			
			if (explicitClasses.Contains(className))
			{
				isNsfw = true;
				triggeredClasses.Add(className);
			}
		}

		if (isNsfw)
		{
			WinNsfwScan.AppLogger.Info($"NudeNetClient.IsNsfwAsync(bytes) NSFW triggered by: {string.Join(", ", triggeredClasses)}");
		}

		return isNsfw;
	}

	public void Dispose() {
		WinNsfwScan.AppLogger.Info("NudeNetClient.Dispose entered");
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

		WinNsfwScan.AppLogger.Info("NudeNetClient.Dispose completed");
	}
}