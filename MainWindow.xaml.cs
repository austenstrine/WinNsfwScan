using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinNsfwScan;

public partial class MainWindow : Window {
	private const int WH_KEYBOARD_LL = 13;
	private const int WM_KEYDOWN = 0x0100;
	private const int WM_SYSKEYDOWN = 0x0104;
	private const int VK_ESCAPE = 0x1B;
	private const int VK_TAB = 0x09;
	private const int VK_LWIN = 0x5B;
	private const int VK_RWIN = 0x5C;
	private const int VK_MENU = 0x12;
	private const int VK_CONTROL = 0x11;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetCursorPos(int X, int Y);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);

	private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

	private readonly DispatcherTimer _hardBlockTimer;
	private LowLevelKeyboardProc? _keyboardProc;
	private IntPtr _keyboardHookHandle = IntPtr.Zero;
	private DateTime _hardBlockUntilUtc;
	private bool _hardBlockFullscreenApplied;
	private WindowStyle _savedWindowStyle;
	private ResizeMode _savedResizeMode;
	private WindowState _savedWindowState;
	private bool _savedTopmost;
	private bool _webUiLoaded;

	public bool IsHardBlockActive => DateTime.UtcNow < _hardBlockUntilUtc;
	public bool IsWebUiLoaded => _webUiLoaded;
	public IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

	public MainWindow() {
		//AppLogger.Info("MainWindow.ctor entered");
		InitializeComponent();
		Loaded += MainWindow_Loaded;
		Closing += MainWindow_Closing;
		StateChanged += MainWindow_StateChanged;

		_hardBlockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
		_hardBlockTimer.Tick += HardBlockTimer_Tick;
		//AppLogger.Info("MainWindow.ctor completed");
	}

	protected override void OnSourceInitialized(EventArgs e) {
		base.OnSourceInitialized(e);

		IntPtr hwnd = WindowHandle;
		if(hwnd != IntPtr.Zero) {
			bool excluded = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
			AppLogger.Info($"MainWindow capture exclusion applied={excluded}");
		}
	}

	public void ActivateHardBlock(TimeSpan duration) {
		var targetUntil = DateTime.UtcNow.Add(duration);
		if(targetUntil > _hardBlockUntilUtc) {
			_hardBlockUntilUtc = targetUntil;
		}

		NotifyFrontendHardBlockStart(duration);

		if(!_hardBlockTimer.IsEnabled) {
			AppLogger.Info($"MainWindow.ActivateHardBlock started for {duration.TotalSeconds:F0}s");
			_hardBlockTimer.Start();
		}

		EnsureKeyboardHook();
		_ = MoveCursorAwayThenEnforceHardBlockAsync();
	}

	private async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
		//AppLogger.Info("MainWindow.MainWindow_Loaded entered");
		string webUiFolder = Path.Combine(AppContext.BaseDirectory, "webui");

		if(!Directory.Exists(webUiFolder)) {
			AppLogger.Error($"MainWindow.MainWindow_Loaded missing webui folder at {webUiFolder}");
			System.Windows.MessageBox.Show(
				$"Frontend folder not found at:\n{webUiFolder}",
				"WinNsfwScan",
				MessageBoxButton.OK,
				MessageBoxImage.Warning
			);
			return;
		}

		try {
			await webView.EnsureCoreWebView2Async();
			//AppLogger.Info("MainWindow.MainWindow_Loaded WebView2 initialized");

			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				"app.local",
				webUiFolder,
				Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow
			);

			// Listen for "ready" message from the frontend
			webView.CoreWebView2.WebMessageReceived += (s, args) => {
				if(args.TryGetWebMessageAsString() == "app-ready") {
					//AppLogger.Info("MainWindow.MainWindow_Loaded frontend app-ready received");
					_webUiLoaded = true;
					// Frontend is ready → show WebView2 and hide loader
					Dispatcher.Invoke(() => {
						webView.Visibility = Visibility.Visible;
						LoadingOverlay.Visibility = Visibility.Collapsed;
					});
				}
			};

			webView.CoreWebView2.NavigationStarting += (s, args) => {
				if(!IsHardBlockActive)
					return;

				if(!IsAppLocalUri(args.Uri)) {
					args.Cancel = true;
					AppLogger.Info($"MainWindow blocked navigation during hard block uri={args.Uri}");
				}
			};

			webView.CoreWebView2.Navigate("http://app.local/index.html");
			//AppLogger.Info("MainWindow.MainWindow_Loaded navigation started");
		}
		catch(Exception ex) {
			AppLogger.Error("MainWindow.MainWindow_Loaded failed", ex);
			System.Windows.MessageBox.Show(
				$"Failed to initialize WebView2:\n{ex.Message}",
				"Error",
				MessageBoxButton.OK,
				MessageBoxImage.Error
			);
		}
	}

	private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
		//AppLogger.Info("MainWindow.MainWindow_Closing entered");
		e.Cancel = true;

		if(!_webUiLoaded) {
			AppLogger.Info("MainWindow closing blocked until web UI loads");
			Show();
			Activate();
			return;
		}

		if(IsHardBlockActive) {
			AppLogger.Info("MainWindow closing blocked during hard block");
			EnforceHardBlockPresentation();
			return;
		}

		this.Hide();
	}

	private void MainWindow_StateChanged(object? sender, EventArgs e) {
		if(!IsHardBlockActive)
			return;

		if(WindowState != WindowState.Maximized) {
			EnforceHardBlockPresentation();
		}
	}

	private void HardBlockTimer_Tick(object? sender, EventArgs e) {
		if(!IsHardBlockActive) {
			_hardBlockTimer.Stop();
			RemoveKeyboardHook();
			RestoreAfterHardBlock();
			NotifyFrontendHardBlockEnd();
			AppLogger.Info("MainWindow hard block ended");
			return;
		}

		EnforceHardBlockPresentation();
	}

	private void EnforceHardBlockPresentation() {
		if(!_hardBlockFullscreenApplied) {
			_savedWindowStyle = WindowStyle;
			_savedResizeMode = ResizeMode;
			_savedWindowState = WindowState;
			_savedTopmost = Topmost;

			WindowStyle = WindowStyle.None;
			ResizeMode = ResizeMode.NoResize;
			_hardBlockFullscreenApplied = true;
			AppLogger.Info("MainWindow entered hard-block fullscreen mode");
		}

		if(!IsVisible) {
			Show();
		}

		if(WindowState == WindowState.Minimized) {
			WindowState = WindowState.Normal;
		}

		WindowState = WindowState.Maximized;
		Topmost = true;
		Activate();
		Focus();
		Topmost = false;
	}

	private void RestoreAfterHardBlock() {
		if(!_hardBlockFullscreenApplied)
			return;

		WindowStyle = _savedWindowStyle;
		ResizeMode = _savedResizeMode;
		WindowState = _savedWindowState;
		Topmost = _savedTopmost;
		_hardBlockFullscreenApplied = false;
		AppLogger.Info("MainWindow restored from hard-block fullscreen mode");
	}

	private void NotifyFrontendHardBlockStart(TimeSpan duration) {
		TryPostWebMessage($"{{\"type\":\"hard-block-start\",\"durationSeconds\":{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))}}}");
	}

	private void NotifyFrontendHardBlockEnd() {
		TryPostWebMessage("{\"type\":\"hard-block-end\"}");
	}

	private void TryPostWebMessage(string json) {
		try {
			webView.CoreWebView2?.PostWebMessageAsJson(json);
		}
		catch(Exception ex) {
			AppLogger.Error("MainWindow failed to post frontend hard-block message", ex);
		}
	}

	private async Task MoveCursorAwayThenEnforceHardBlockAsync() {
		if(!GetCursorPos(out var originalPosition)) {
			AppLogger.Info("MainWindow cursor move skipped because current cursor position could not be read");
			EnforceHardBlockPresentation();
			return;
		}

		int topRightX = (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 1);
		int topRightY = (int)SystemParameters.VirtualScreenTop;

		if(!SetCursorPos(topRightX, topRightY)) {
			AppLogger.Info("MainWindow cursor move failed to reach top-right");
			EnforceHardBlockPresentation();
			return;
		}

		await Task.Delay(250);

		EnforceHardBlockPresentation();

		await Task.Delay(150);

		if(!SetCursorPos(originalPosition.X, originalPosition.Y)) {
			AppLogger.Info("MainWindow cursor move failed to restore original position");
		}
	}

	private void EnsureKeyboardHook() {
		if(_keyboardHookHandle != IntPtr.Zero)
			return;

		_keyboardProc = KeyboardHookCallback;
		IntPtr moduleHandle = GetModuleHandle(null);
		_keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
		AppLogger.Info($"MainWindow keyboard hook install result={( _keyboardHookHandle != IntPtr.Zero ? "ok" : "failed" )}");
	}

	private void RemoveKeyboardHook() {
		if(_keyboardHookHandle == IntPtr.Zero)
			return;

		bool removed = UnhookWindowsHookEx(_keyboardHookHandle);
		AppLogger.Info($"MainWindow keyboard hook removed={removed}");
		_keyboardHookHandle = IntPtr.Zero;
		_keyboardProc = null;
	}

	private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
		if(nCode >= 0 && IsHardBlockActive) {
			int message = wParam.ToInt32();
			if(message == WM_KEYDOWN || message == WM_SYSKEYDOWN) {
				var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
				bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
				bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

				if(info.vkCode == VK_ESCAPE && ctrlDown) {
					AppLogger.Info("MainWindow blocked Ctrl+Esc during hard block");
					return (IntPtr)1;
				}

				if(info.vkCode == VK_LWIN || info.vkCode == VK_RWIN) {
					AppLogger.Info($"MainWindow blocked Windows key vk=0x{info.vkCode:X2} during hard block");
					return (IntPtr)1;
				}

				if(info.vkCode == VK_TAB && altDown) {
					AppLogger.Info("MainWindow blocked Alt+Tab during hard block");
					return (IntPtr)1;
				}
			}
		}

		return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
	}

	protected override void OnClosed(EventArgs e) {
		RemoveKeyboardHook();
		base.OnClosed(e);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KBDLLHOOKSTRUCT {
		public uint vkCode;
		public uint scanCode;
		public uint flags;
		public uint time;
		public IntPtr dwExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT {
		public int X;
		public int Y;
	}

	private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

	private static bool IsAppLocalUri(string? uri) {
		if(string.IsNullOrWhiteSpace(uri))
			return false;

		if(!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
			return false;

		return string.Equals(parsed.Host, "app.local", StringComparison.OrdinalIgnoreCase);
	}
}