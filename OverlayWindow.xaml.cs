using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNsfwScan;

public partial class OverlayWindow : Window {
	// Win32 constants for making the window click-through
	const int GWL_EXSTYLE     = -20;
	const int WS_EX_LAYERED   = 0x00080000;
	const int WS_EX_TRANSPARENT = 0x00000020;

	// Virtual-key codes for Ctrl and Alt
	const int VK_CONTROL = 0x11;
	const int VK_MENU    = 0x12;

	[DllImport("user32.dll")] static extern int   GetWindowLong(IntPtr hwnd, int index);
	[DllImport("user32.dll")] static extern int   SetWindowLong(IntPtr hwnd, int index, int newStyle);
	[DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
	[DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
	[DllImport("user32.dll")] static extern bool  SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

	static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
	const uint SWP_NOMOVE    = 0x0002;
	const uint SWP_NOSIZE    = 0x0001;
	const uint SWP_NOACTIVATE = 0x0010;
	const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

	private IntPtr _hwnd;
	private bool   _isClickThrough = true;
	private bool _fullScreenBlockActive;
	private double _dpiScaleX = 1.0;
	private double _dpiScaleY = 1.0;
	private const double BoxInflationScale = 1.10;

	private readonly DispatcherTimer _modifierTimer;

	public OverlayWindow() {
		//AppLogger.Info("OverlayWindow.ctor entered");
		InitializeComponent();

		_modifierTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
		_modifierTimer.Tick += OnModifierTimerTick;

		// WPF's internal layout pass (called after OnSourceInitialized) uses HWND_TOP,
		// which silently demotes the window from the topmost Z-band.  Re-assert TOPMOST
		// here, after WPF has finished its first layout/render.
		ContentRendered += (_, _) =>
			SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

		//AppLogger.Info("OverlayWindow.ctor completed");
	}

	protected override void OnSourceInitialized(EventArgs e) {
		base.OnSourceInitialized(e);

		_hwnd = new WindowInteropHelper(this).Handle;

		var source = PresentationSource.FromVisual(this);
		if(source?.CompositionTarget != null) {
			// TransformToDevice.M11/M22 give us physical pixels per logical pixel
			_dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
			_dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
		}

		SetClickThrough(true);
		_modifierTimer.Start();

		// Cover the full primary screen.
		// AllowsTransparency+WindowState.Maximized only reaches the work area (excludes taskbar).
		Left   = SystemParameters.VirtualScreenLeft;
		Top    = SystemParameters.VirtualScreenTop;
		Width  = SystemParameters.PrimaryScreenWidth;
		Height = SystemParameters.PrimaryScreenHeight;

		// Explicitly assert topmost at Win32 level — more reliable than WPF's Topmost property alone.
		SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
		SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

		//AppLogger.Info($"OverlayWindow.OnSourceInitialized hwnd={_hwnd} dpiX={_dpiScaleX} dpiY={_dpiScaleY}");
	}

	// ── Click-through toggle ──────────────────────────────────────────────────

	private void SetClickThrough(bool clickThrough) {
		int style = GetWindowLong(_hwnd, GWL_EXSTYLE);

		if(clickThrough) {
			style |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
		} else {
			style = (style | WS_EX_LAYERED) & ~WS_EX_TRANSPARENT;
		}

		SetWindowLong(_hwnd, GWL_EXSTYLE, style);
		_isClickThrough = clickThrough;
	}

	// Polls keyboard state every 50 ms; disables click-through while Ctrl+Alt is held
	private void OnModifierTimerTick(object? sender, EventArgs e) {
		// Re-assert topmost every tick so activating other windows can't push us down.
		SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

		if(_fullScreenBlockActive) {
			if(_isClickThrough) {
				SetClickThrough(false);
			}
			return;
		}

		bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
		bool alt  = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
		bool shouldBeClickThrough = !(ctrl && alt);

		if(shouldBeClickThrough != _isClickThrough) {
			//AppLogger.Info($"OverlayWindow.OnModifierTimerTick toggling clickthrough={shouldBeClickThrough} (ctrl={ctrl} alt={alt})");
			SetClickThrough(shouldBeClickThrough);
		}
	}

	// Fires only when click-through is off (i.e. Ctrl+Alt is held)
	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
		base.OnMouseLeftButtonDown(e);

		bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
		bool alt  = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;

		if(!_isClickThrough && ctrl && alt) {
			AppLogger.Info("OverlayWindow.OnMouseLeftButtonDown Ctrl+Alt+Click — clearing all boxes");
			ClearAll();
		}
	}

	// ── Box management ────────────────────────────────────────────────────────

	/// <summary>
	/// Replaces all censorship rectangles with the provided detections.
	/// </summary>
	public void ReplaceBoxes(NudeNetDetection[] detections) {
		if(_fullScreenBlockActive)
			return;

		BoxCanvas.Children.Clear();
		AddBoxes(detections);
	}

	/// <summary>
	/// Appends black censorship rectangles for each detection.
	/// Coordinates are in source-image physical pixels and are converted to WPF logical units.
	/// </summary>
	public void AddBoxes(NudeNetDetection[] detections) {
		if(_fullScreenBlockActive)
			return;

		AppLogger.Info($"OverlayWindow.AddBoxes adding {detections.Length} boxes (total children={BoxCanvas.Children.Count + detections.Length})");

		foreach(var d in detections) {
			double logicalX = d.X / _dpiScaleX;
			double logicalY = d.Y / _dpiScaleY;
			double logicalW = d.Width  / _dpiScaleX;
			double logicalH = d.Height / _dpiScaleY;
			double inflatedW = logicalW * BoxInflationScale;
			double inflatedH = logicalH * BoxInflationScale;
			double expandLeft = (inflatedW - logicalW) * 0.5;
			double expandTop = (inflatedH - logicalH) * 0.5;

			var rect = new System.Windows.Shapes.Rectangle {
				Fill   = System.Windows.Media.Brushes.Black,
				Width  = Math.Max(inflatedW, 1),
				Height = Math.Max(inflatedH, 1),
			};

			Canvas.SetLeft(rect, logicalX - expandLeft);
			Canvas.SetTop(rect,  logicalY - expandTop);
			BoxCanvas.Children.Add(rect);
		}
	}

	/// <summary>
	/// Removes all censorship boxes and re-enables click-through.
	/// Does NOT close the window; it stays alive for subsequent detections.
	/// </summary>
	public void ClearAll() {
		AppLogger.Info("OverlayWindow.ClearAll clearing all boxes");
		_fullScreenBlockActive = false;
		RootGrid.Background = System.Windows.Media.Brushes.Transparent;
		BoxCanvas.Children.Clear();
		SetClickThrough(true);
	}

	public void ShowFullScreenBlock() {
		if(_fullScreenBlockActive)
			return;

		AppLogger.Info("OverlayWindow.ShowFullScreenBlock activating full-screen block");
		_fullScreenBlockActive = true;
		BoxCanvas.Children.Clear();
		RootGrid.Background = System.Windows.Media.Brushes.Black;
		SetClickThrough(false);
	}

	protected override void OnClosed(EventArgs e) {
		//AppLogger.Info("OverlayWindow.OnClosed entered");
		_modifierTimer.Stop();
		base.OnClosed(e);
	}
}
