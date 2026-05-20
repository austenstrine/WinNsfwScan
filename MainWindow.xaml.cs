using System;
using System.IO;
using System.Windows;

namespace WinNsfwScan;

public partial class MainWindow : Window {
	public MainWindow() {
		InitializeComponent();
		Loaded += MainWindow_Loaded;
		Closing += MainWindow_Closing;
	}

	private async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
		string webUiFolder = Path.Combine(AppContext.BaseDirectory, "webui");

		if(!Directory.Exists(webUiFolder)) {
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

			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				"app.local",
				webUiFolder,
				Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow
			);

			// Listen for "ready" message from the frontend
			webView.CoreWebView2.WebMessageReceived += (s, args) => {
				if(args.TryGetWebMessageAsString() == "app-ready") {
					// Frontend is ready → show WebView2 and hide loader
					Dispatcher.Invoke(() => {
						webView.Visibility = Visibility.Visible;
						LoadingOverlay.Visibility = Visibility.Collapsed;
					});
				}
			};

			webView.CoreWebView2.Navigate("http://app.local/index.html");
		}
		catch(Exception ex) {
			System.Windows.MessageBox.Show(
				$"Failed to initialize WebView2:\n{ex.Message}",
				"Error",
				MessageBoxButton.OK,
				MessageBoxImage.Error
			);
		}
	}

	private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
		e.Cancel = true;
		this.Hide();
	}
}