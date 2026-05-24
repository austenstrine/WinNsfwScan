using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinNsfwScan;

public partial class MainWindow : Window {
	private readonly DispatcherTimer _hardBlockTimer;
	private DateTime _hardBlockUntilUtc;

	public bool IsHardBlockActive => DateTime.UtcNow < _hardBlockUntilUtc;
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

	public void ActivateHardBlock(TimeSpan duration) {
		var targetUntil = DateTime.UtcNow.Add(duration);
		if(targetUntil > _hardBlockUntilUtc) {
			_hardBlockUntilUtc = targetUntil;
		}

		if(!_hardBlockTimer.IsEnabled) {
			AppLogger.Info($"MainWindow.ActivateHardBlock started for {duration.TotalSeconds:F0}s");
			_hardBlockTimer.Start();
		}

		EnforceHardBlockPresentation();
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
			AppLogger.Info("MainWindow hard block ended");
			return;
		}

		EnforceHardBlockPresentation();
	}

	private void EnforceHardBlockPresentation() {
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

	private static bool IsAppLocalUri(string? uri) {
		if(string.IsNullOrWhiteSpace(uri))
			return false;

		if(!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
			return false;

		return string.Equals(parsed.Host, "app.local", StringComparison.OrdinalIgnoreCase);
	}
}