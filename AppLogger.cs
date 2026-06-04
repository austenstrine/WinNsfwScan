using System;
using System.Collections.Generic;
using System.IO;

namespace WinNsfwScan;

public static class AppLogger {
	private static readonly object _sync = new();
	private static readonly string _sessionStamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
	private static readonly string _logRoot = BuildLogRoot();
	private static readonly string _logFilePath = BuildLogPath();
	private static readonly Dictionary<string, string> _modelLogFilePaths = new(StringComparer.OrdinalIgnoreCase) {
		["erax"] = Path.Combine(_logRoot, $"runtime-{_sessionStamp}-erax.log"),
		["nudenet"] = Path.Combine(_logRoot, $"runtime-{_sessionStamp}-nudenet.log"),
		["nsfwsharp"] = Path.Combine(_logRoot, $"runtime-{_sessionStamp}-nsfwsharp.log"),
	};

	public static string LogFilePath => _logFilePath;
	public static string EraxLogFilePath => _modelLogFilePaths["erax"];
	public static string NudeNetLogFilePath => _modelLogFilePaths["nudenet"];
	public static string NsfwSharpLogFilePath => _modelLogFilePaths["nsfwsharp"];

	public static void Info(string message) => Write("INFO", message);
	public static void ModelInfo(string model, string message) => WriteModel("INFO", model, message);

	public static void Error(string message, Exception? ex = null) {
		if(ex == null) {
			Write("ERROR", message);
			return;
		}

		Write("ERROR", $"{message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
	}

	public static void ModelError(string model, string message, Exception? ex = null) {
		if(ex == null) {
			WriteModel("ERROR", model, message);
			return;
		}

		WriteModel("ERROR", model, $"{message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
	}

	private static void Write(string level, string message) {
		string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}";

		lock(_sync) {
			try {
				Directory.CreateDirectory(_logRoot);
				File.AppendAllText(_logFilePath, line + Environment.NewLine);
			}
			catch { }
		}

		Console.WriteLine(line);
	}

	private static void WriteModel(string level, string model, string message) {
		string normalized = NormalizeModel(model);
		if(!_modelLogFilePaths.TryGetValue(normalized, out string? modelPath)) {
			Write(level, $"[model:{normalized}] {message}");
			return;
		}

		string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}";
		lock(_sync) {
			try {
				Directory.CreateDirectory(_logRoot);
				File.AppendAllText(modelPath, line + Environment.NewLine);
			}
			catch { }
		}
	}

	private static string NormalizeModel(string model) {
		if(string.IsNullOrWhiteSpace(model))
			return "unknown";
		return model.Trim().ToLowerInvariant();
	}

	private static string BuildLogRoot() {
		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"WinNsfwScan",
			"logs"
		);
}

	private static string BuildLogPath() {
		return Path.Combine(_logRoot, $"runtime-{_sessionStamp}.log");
	}
}