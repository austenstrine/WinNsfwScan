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
		// Read config
		string configPath = Path.Combine(AppContext.BaseDirectory, "backend.json");
		string json = File.ReadAllText(configPath);
		using var doc = JsonDocument.Parse(json);
		string serverExecutable = doc.RootElement.GetProperty("ServerExecutable").GetString()!;

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

		string? line = _process.StandardOutput.ReadLine();
		if (line != null && line.StartsWith("PORT:")) {
			_port = int.Parse(line.Split(':')[1]);
		}
		else {
			throw new Exception("Failed to read port from backend");
		}

		_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		Console.CancelKeyPress += OnCancelKeyPress;

		Console.WriteLine($"[NudeNet] Backend started on port {_port}");
	}

	private void OnProcessExit(object? sender, EventArgs e) => Dispose();
	private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) => Dispose();

	public async Task<bool> IsNsfwAsync(string imagePath) {
		using var content = new MultipartFormDataContent();
		var fileBytes = await File.ReadAllBytesAsync(imagePath);
		content.Add(new ByteArrayContent(fileBytes), "file", Path.GetFileName(imagePath));

		var sw = Stopwatch.StartNew();
		var response = await _httpClient.PostAsync($"http://127.0.0.1:{_port}/detect", content);
		sw.Stop();
		Console.WriteLine($"[NudeNet] Round-trip time: {sw.ElapsedMilliseconds} ms");
		
		var json = await response.Content.ReadAsStringAsync();
		Console.WriteLine($"[NudeNet] Response: {json}");

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
			Console.WriteLine($"[NudeNet] NSFW triggered by: {string.Join(", ", triggeredClasses)}");
		}

		return isNsfw;
	}

	public void Dispose() {
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

		Console.WriteLine("[NudeNet] Backend stopped");
	}
}