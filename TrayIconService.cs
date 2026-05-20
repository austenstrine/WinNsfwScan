using System;
using System.Windows;
using System.Windows.Forms;

public class TrayIconService : IDisposable {
	private readonly NotifyIcon _notifyIcon;

	public TrayIconService() {
		_notifyIcon = new NotifyIcon {
			Icon = System.Drawing.SystemIcons.Application, // You can replace this with a real .ico later
			Visible = true,
			Text = "WinNsfwScan"
		};

		var contextMenu = new ContextMenuStrip();
		var exitItem = new ToolStripMenuItem("Exit");
		exitItem.Click += (s, e) => System.Windows.Application.Current.Shutdown();
		contextMenu.Items.Add(exitItem);

		_notifyIcon.ContextMenuStrip = contextMenu;
	}

	public void Dispose() {
		_notifyIcon.Visible = false;
		_notifyIcon.Dispose();
	}
}