using System;
using System.Windows.Forms;

namespace WinNsfwScan;

public class TrayIconService : IDisposable {
	private readonly NotifyIcon _notifyIcon;

	public TrayIconService() {
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
	}

	public void Dispose() {
		_notifyIcon.Visible = false;
		_notifyIcon.Dispose();
	}
}