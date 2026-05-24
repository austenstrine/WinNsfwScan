using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WinNsfwScan;

internal static class WindowMinimizerService {
	private const int GA_ROOT = 2;
	private const int SW_MINIMIZE = 6;

	[DllImport("user32.dll")]
	private static extern IntPtr WindowFromPoint(POINT point);

	[DllImport("user32.dll")]
	private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern int GetWindowTextLength(IntPtr hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

	[DllImport("user32.dll")]
	private static extern IntPtr GetShellWindow();

	public static bool TryMinimizeWindowAtPoint(int screenX, int screenY, IReadOnlyCollection<IntPtr> excludedWindows, out IntPtr minimizedWindow, out string windowTitle) {
		minimizedWindow = IntPtr.Zero;
		windowTitle = string.Empty;

		IntPtr hwnd = WindowFromPoint(new POINT { X = screenX, Y = screenY });
		if(hwnd == IntPtr.Zero)
			return false;

		IntPtr root = GetAncestor(hwnd, GA_ROOT);
		if(root == IntPtr.Zero)
			return false;

		if(excludedWindows.Contains(root))
			return false;

		if(root == GetShellWindow())
			return false;

		if(!IsWindowVisible(root) || IsIconic(root))
			return false;

		string className = GetWindowClassName(root);
		if(string.Equals(className, "Progman", StringComparison.Ordinal)
			|| string.Equals(className, "WorkerW", StringComparison.Ordinal)
			|| string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal)) {
			return false;
		}

		windowTitle = GetWindowTitle(root);
		if(ShowWindow(root, SW_MINIMIZE)) {
			minimizedWindow = root;
			return true;
		}

		return false;
	}

	private static string GetWindowTitle(IntPtr hwnd) {
		int length = GetWindowTextLength(hwnd);
		if(length <= 0)
			return string.Empty;

		var buffer = new StringBuilder(length + 1);
		GetWindowText(hwnd, buffer, buffer.Capacity);
		return buffer.ToString();
	}

	private static string GetWindowClassName(IntPtr hwnd) {
		var buffer = new StringBuilder(256);
		int copied = GetClassName(hwnd, buffer, buffer.Capacity);
		return copied > 0 ? buffer.ToString() : string.Empty;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT {
		public int X;
		public int Y;
	}
}