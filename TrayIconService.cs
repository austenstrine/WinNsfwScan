using System;
using System.Windows.Forms;

namespace WinNsfwScan;

public class TrayIconService : IDisposable {
	private readonly NotifyIcon _notifyIcon;

	public TrayIconService() {
		AppLogger.Info("TrayIconService.ctor entered");
		_notifyIcon = new NotifyIcon {
			Icon = SystemIcons.Application,
			Visible = true,
			Text = "WinNsfwScan"
		};

		// Double click opens the window
		_notifyIcon.DoubleClick += (s, e) =>
			((App)System.Windows.Application.Current)?.ShowMainWindow();

		var contextMenu = new ContextMenuStrip();

		var openItem = new ToolStripMenuItem("Open");
		openItem.Click += (s, e) =>
			((App)System.Windows.Application.Current)?.ShowMainWindow();

		var exitItem = new ToolStripMenuItem("Exit");
		exitItem.Click += (s, e) => System.Windows.Application.Current.Shutdown();

		contextMenu.Items.Add(openItem);
		contextMenu.Items.Add(new ToolStripSeparator());
		contextMenu.Items.Add(exitItem);

		_notifyIcon.ContextMenuStrip = contextMenu;
		AppLogger.Info("TrayIconService.ctor completed");
	}

	public void Dispose() {
		AppLogger.Info("TrayIconService.Dispose entered");
		_notifyIcon.Visible = false;
		_notifyIcon.Dispose();
		AppLogger.Info("TrayIconService.Dispose completed");
	}
}