using System;
using System.IO;
using System.Windows;

namespace WinNsfwScan;

public partial class MainWindow : Window {
	public MainWindow() {
		//AppLogger.Info("MainWindow.ctor entered");
		InitializeComponent();
		Loaded += MainWindow_Loaded;
		Closing += MainWindow_Closing;
		//AppLogger.Info("MainWindow.ctor completed");
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
		this.Hide();
	}
}