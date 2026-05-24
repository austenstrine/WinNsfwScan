using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace WinNsfwScan;

/// <summary>
/// Two-layer resurrection guard.
///
/// Normal mode: call <see cref="EnsureWatchdog"/> once on startup. This spawns a hidden
/// child process running this same exe with the "--watchdog &lt;pid&gt;" flag. That child
/// watches the parent PID and restarts it when it exits — surviving Task Manager kills.
///
/// To turn off: call <see cref="RequestDisable"/>. A 30-minute cooldown begins; after it
/// expires the watchdog will no longer restart the process and the app exits cleanly.
///
/// Debug: call <see cref="ForceExit"/> to terminate immediately without restart.
/// </summary>
internal static class WatchdogService {
	private const string WatchdogFlag = "--watchdog";
	private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

	private static readonly string DataFolder = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"WinNsfwScan");

	private static readonly string DisableFile   = Path.Combine(DataFolder, "protection-disable-requested.txt");
	private static readonly string ForceExitFile = Path.Combine(DataFolder, "force-exit.flag");

	// ── Main-app side ─────────────────────────────────────────────────────

	/// <summary>Spawns a watchdog child process that will restart this process if it exits unexpectedly.</summary>
	public static void EnsureWatchdog() {
		string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
		if(exePath == null) {
			AppLogger.Info("WatchdogService.EnsureWatchdog could not determine exe path, skipping");
			return;
		}

		var psi = new ProcessStartInfo(exePath, $"{WatchdogFlag} {Environment.ProcessId}") {
			CreateNoWindow = true,
			UseShellExecute = false,
		};

		try {
			Process.Start(psi);
			AppLogger.Info($"WatchdogService.EnsureWatchdog spawned watchdog for pid={Environment.ProcessId}");
		}
		catch(Exception ex) {
			AppLogger.Error("WatchdogService.EnsureWatchdog failed to spawn watchdog", ex);
		}
	}

	/// <summary>
	/// Begins the 30-minute cooldown after which protection will be disabled.
	/// Ignored if a cooldown is already running.
	/// </summary>
	public static void RequestDisable() {
		var (isRequested, secondsRemaining) = GetCooldownState();
		if(isRequested && secondsRemaining > 0) {
			AppLogger.Info("WatchdogService.RequestDisable cooldown already active, ignoring duplicate request");
			return;
		}

		try {
			Directory.CreateDirectory(DataFolder);
			File.WriteAllText(DisableFile, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
			AppLogger.Info("WatchdogService.RequestDisable wrote disable request");
		}
		catch(Exception ex) {
			AppLogger.Error("WatchdogService.RequestDisable failed", ex);
		}
	}

	/// <summary>Returns whether a disable was requested and how many seconds remain in the cooldown (0 = expired).</summary>
	public static (bool isRequested, int secondsRemaining) GetCooldownState() {
		if(!File.Exists(DisableFile))
			return (false, 0);

		try {
			string content = File.ReadAllText(DisableFile).Trim();
			if(!DateTime.TryParse(content, null, DateTimeStyles.RoundtripKind, out var requestedAt))
				return (false, 0);

			var remaining = CooldownDuration - (DateTime.UtcNow - requestedAt);
			return (true, (int)Math.Max(0, Math.Ceiling(remaining.TotalSeconds)));
		}
		catch {
			return (false, 0);
		}
	}

	/// <summary>Returns true if a disable was requested and the 30-minute cooldown has fully expired.</summary>
	public static bool IsProtectionDisabled() {
		var (isRequested, secondsRemaining) = GetCooldownState();
		return isRequested && secondsRemaining == 0;
	}

	/// <summary>Deletes the disable-request file so protection is active again on the next app start.</summary>
	public static void DeleteDisableFile() {
		try {
			if(File.Exists(DisableFile))
				File.Delete(DisableFile);
		}
		catch { }
	}

	/// <summary>
	/// Debug-only: terminates this process immediately without the watchdog restarting it.
	/// Writes a flag file that the watchdog honours.
	/// </summary>
	public static void ForceExit() {
		AppLogger.Info("WatchdogService.ForceExit debug kill invoked");
		try {
			Directory.CreateDirectory(DataFolder);
			File.WriteAllText(ForceExitFile, "1");
		}
		catch { }
		Environment.Exit(0);
	}

	// ── Watchdog mode ─────────────────────────────────────────────────────

	/// <summary>Returns true if the process was launched in watchdog mode and populates mainPid.</summary>
	public static bool IsWatchdogMode(string[] args, out int mainPid) {
		mainPid = 0;
		return args.Length >= 2
			&& string.Equals(args[0], WatchdogFlag, StringComparison.Ordinal)
			&& int.TryParse(args[1], out mainPid);
	}

	/// <summary>
	/// Blocking watchdog entry point. Waits for the monitored process to exit, then restarts it
	/// unless the force-exit flag is set or the protection cooldown has expired.
	/// </summary>
	public static void RunAsWatchdog(int mainPid) {
		AppLogger.Info($"WatchdogService.RunAsWatchdog watching pid={mainPid}");

		try {
			using var process = Process.GetProcessById(mainPid);
			process.WaitForExit();
		}
		catch(ArgumentException) {
			AppLogger.Info($"WatchdogService.RunAsWatchdog pid={mainPid} not found");
		}
		catch(Exception ex) {
			AppLogger.Error("WatchdogService.RunAsWatchdog error waiting for main process", ex);
		}

		AppLogger.Info($"WatchdogService.RunAsWatchdog pid={mainPid} exited, checking restart conditions");

		if(CheckAndClearForceExit()) {
			AppLogger.Info("WatchdogService.RunAsWatchdog force-exit flag set, not restarting");
			return;
		}

		if(IsProtectionDisabled()) {
			AppLogger.Info("WatchdogService.RunAsWatchdog cooldown expired, not restarting");
			return;
		}

		Thread.Sleep(RestartDelay);
		Restart();
	}

	private static bool CheckAndClearForceExit() {
		try {
			if(!File.Exists(ForceExitFile)) return false;
			File.Delete(ForceExitFile);
			return true;
		}
		catch { return false; }
	}

	private static void Restart() {
		string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
		if(exePath == null) {
			AppLogger.Error("WatchdogService.Restart could not determine exe path");
			return;
		}

		try {
			Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
			AppLogger.Info("WatchdogService.Restart restarted main process");
		}
		catch(Exception ex) {
			AppLogger.Error("WatchdogService.Restart failed", ex);
		}
	}
}
