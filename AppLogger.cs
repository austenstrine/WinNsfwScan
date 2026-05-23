using System;
using System.IO;

namespace WinNsfwScan;

public static class AppLogger {
	private static readonly object _sync = new();
	private static readonly string _logFilePath = BuildLogPath();

	public static string LogFilePath => _logFilePath;

	public static void Info(string message) => Write("INFO", message);

	public static void Error(string message, Exception? ex = null) {
		if(ex == null) {
			Write("ERROR", message);
			return;
		}

		Write("ERROR", $"{message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
	}

	private static void Write(string level, string message) {
		string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}";

		lock(_sync) {
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
				File.AppendAllText(_logFilePath, line + Environment.NewLine);
			}
			catch { }
		}

		Console.WriteLine(line);
	}

	private static string BuildLogPath() {
		string root = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"WinNsfwScan",
			"logs"
		);

		return Path.Combine(root, $"runtime-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
	}
}