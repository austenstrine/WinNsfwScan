using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
	[DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
	[DllImport("user32.dll")] static extern bool  SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

	static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
	const uint SWP_NOMOVE    = 0x0002;
	const uint SWP_NOSIZE    = 0x0001;
	const uint SWP_NOACTIVATE = 0x0010;

	private IntPtr _hwnd;
	private bool   _isClickThrough = true;
	private double _dpiScaleX = 1.0;
	private double _dpiScaleY = 1.0;

	private readonly DispatcherTimer _modifierTimer;

	public OverlayWindow() {
		//AppLogger.Info("OverlayWindow.ctor entered");
		InitializeComponent();

		_modifierTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
		_modifierTimer.Tick += OnModifierTimerTick;

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

		if(!_isClickThrough) {
			AppLogger.Info("OverlayWindow.OnMouseLeftButtonDown Ctrl+Alt+Click — clearing all boxes");
			ClearAll();
		}
	}

	// ── Box management ────────────────────────────────────────────────────────

	/// <summary>
	/// Appends black censorship rectangles for each detection.
	/// Coordinates are in source-image physical pixels and are converted to WPF logical units.
	/// </summary>
	public void AddBoxes(NudeNetDetection[] detections) {
		AppLogger.Info($"OverlayWindow.AddBoxes adding {detections.Length} boxes (total children={BoxCanvas.Children.Count + detections.Length})");

		foreach(var d in detections) {
			double logicalX = d.X / _dpiScaleX;
			double logicalY = d.Y / _dpiScaleY;
			double logicalW = d.Width  / _dpiScaleX;
			double logicalH = d.Height / _dpiScaleY;

			var rect = new System.Windows.Shapes.Rectangle {
				Fill   = System.Windows.Media.Brushes.Black,
				Width  = Math.Max(logicalW, 1),
				Height = Math.Max(logicalH, 1),
			};

			Canvas.SetLeft(rect, logicalX);
			Canvas.SetTop(rect,  logicalY);
			BoxCanvas.Children.Add(rect);
		}
	}

	/// <summary>
	/// Removes all censorship boxes and re-enables click-through.
	/// Does NOT close the window; it stays alive for subsequent detections.
	/// </summary>
	public void ClearAll() {
		AppLogger.Info("OverlayWindow.ClearAll clearing all boxes");
		BoxCanvas.Children.Clear();
		SetClickThrough(true);
	}

	protected override void OnClosed(EventArgs e) {
		//AppLogger.Info("OverlayWindow.OnClosed entered");
		_modifierTimer.Stop();
		base.OnClosed(e);
	}
}
