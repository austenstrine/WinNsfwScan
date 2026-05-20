using System.Windows;
using System.Windows.Input;

namespace WinNsfwScan;

public partial class OverlayWindow : Window {
	public OverlayWindow() {
		InitializeComponent();
	}

	private void OverlayRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
		Close();
	}
}