using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WinNsfwScan;

internal static class WindowsToastService {
	private const string AppId = "WinNsfwScan.Dev";
	private const string ShortcutFileName = "WinNsfwScan Dev.lnk";
	private static bool _initialized;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PROPVARIANT pvar);

	public static void Initialize() {
		if(_initialized)
			return;

		try {
			int hr = SetCurrentProcessExplicitAppUserModelID(AppId);
			if(hr != 0) {
				AppLogger.Error($"WindowsToastService.Initialize failed to set AppUserModelID hr=0x{hr:X8}");
				return;
			}

			EnsureShortcutRegistration();

			_initialized = true;
			AppLogger.Info($"WindowsToastService initialized appId={AppId} shortcut={GetShortcutPath()}");
		}
		catch(Exception ex) {
			AppLogger.Error("WindowsToastService.Initialize failed", ex);
		}
	}

	public static void TryShow(string title, string message) {
		if(!_initialized) {
			Initialize();
			if(!_initialized)
				return;
		}

		try {
			string escapedTitle = SecurityElement.Escape(title) ?? string.Empty;
			string escapedMessage = SecurityElement.Escape(message) ?? string.Empty;

			string xml =
				$"<toast><visual><binding template=\"ToastGeneric\"><text>{escapedTitle}</text><text>{escapedMessage}</text></binding></visual></toast>";

			var doc = new XmlDocument();
			doc.LoadXml(xml);

			var toast = new ToastNotification(doc);
			ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
		}
		catch(Exception ex) {
			AppLogger.Error("WindowsToastService.TryShow failed", ex);
		}
	}

	private static void EnsureShortcutRegistration() {
		string shortcutPath = GetShortcutPath();
		Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

		string executablePath = Environment.ProcessPath
			?? Process.GetCurrentProcess().MainModule?.FileName
			?? throw new InvalidOperationException("Cannot resolve process executable path for toast shortcut registration.");

		object shellLinkObject = new CShellLink();
		IShellLinkW? shellLink = null;
		IPropertyStore? propertyStore = null;
		IPersistFile? persistFile = null;

		try {
			shellLink = (IShellLinkW)shellLinkObject;
			shellLink.SetPath(executablePath);
			shellLink.SetDescription("WinNsfwScan");
			shellLink.SetWorkingDirectory(Path.GetDirectoryName(executablePath)!);
			shellLink.SetIconLocation(executablePath, 0);

			propertyStore = (IPropertyStore)shellLinkObject;
			var appIdProperty = new PROPERTYKEY {
				fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
				pid = 5
			};

			var propVar = PROPVARIANT.FromString(AppId);
			try {
				int setResult = propertyStore.SetValue(ref appIdProperty, ref propVar);
				if(setResult != 0) {
					throw new InvalidOperationException($"IPropertyStore.SetValue failed hr=0x{setResult:X8}");
				}

				int commitResult = propertyStore.Commit();
				if(commitResult != 0) {
					throw new InvalidOperationException($"IPropertyStore.Commit failed hr=0x{commitResult:X8}");
				}
			}
			finally {
				PropVariantClear(ref propVar);
			}

			persistFile = (IPersistFile)shellLinkObject;
			persistFile.Save(shortcutPath, true);
		}
		finally {
			if(persistFile != null) Marshal.ReleaseComObject(persistFile);
			if(propertyStore != null) Marshal.ReleaseComObject(propertyStore);
			if(shellLink != null) Marshal.ReleaseComObject(shellLink);
			Marshal.ReleaseComObject(shellLinkObject);
		}
	}

	private static string GetShortcutPath() {
		string programsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
		return Path.Combine(programsPath, ShortcutFileName);
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private struct PROPERTYKEY {
		public Guid fmtid;
		public uint pid;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct PROPVARIANT {
		[FieldOffset(0)]
		public ushort vt;

		[FieldOffset(8)]
		public IntPtr pointerValue;

		public static PROPVARIANT FromString(string value) {
			return new PROPVARIANT {
				vt = (ushort)VarEnum.VT_LPWSTR,
				pointerValue = Marshal.StringToCoTaskMemUni(value)
			};
		}
	}

	[ComImport]
	[Guid("00021401-0000-0000-C000-000000000046")]
	private class CShellLink;

	[ComImport]
	[Guid("000214F9-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellLinkW {
		void GetPath([Out] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
		void GetIDList(out IntPtr ppidl);
		void SetIDList(IntPtr pidl);
		void GetDescription([Out] System.Text.StringBuilder pszName, int cchMaxName);
		void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
		void GetWorkingDirectory([Out] System.Text.StringBuilder pszDir, int cchMaxPath);
		void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
		void GetArguments([Out] System.Text.StringBuilder pszArgs, int cchMaxPath);
		void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
		void GetHotkey(out short pwHotkey);
		void SetHotkey(short wHotkey);
		void GetShowCmd(out int piShowCmd);
		void SetShowCmd(int iShowCmd);
		void GetIconLocation([Out] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
		void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
		void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
		void Resolve(IntPtr hwnd, uint fFlags);
		void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
	}

	[ComImport]
	[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore {
		int GetCount(out uint cProps);
		int GetAt(uint iProp, out PROPERTYKEY pkey);
		int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
		int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
		int Commit();
	}

	[ComImport]
	[Guid("0000010B-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPersistFile {
		void GetClassID(out Guid pClassID);
		void IsDirty();
		void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
		void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
		void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
		void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
	}
}