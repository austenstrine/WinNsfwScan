using System.Windows;
using System.Windows.Input;

namespace WinNsfwScan;

public partial class OverlayWindow : Window {
	public OverlayWindow() {
		AppLogger.Info("OverlayWindow.ctor entered");
		InitializeComponent();
		AppLogger.Info("OverlayWindow.ctor completed");
	}

	private void OverlayRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
		AppLogger.Info("OverlayWindow.OverlayRoot_MouseLeftButtonDown entered");
		Close();
	}
}