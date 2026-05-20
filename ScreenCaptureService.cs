using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using SkiaSharp;

namespace WinNsfwScan;

public class ScreenCaptureService {
	public SKBitmap? CapturePrimaryScreen() {
		AppLogger.Info("ScreenCaptureService.CapturePrimaryScreen entered");
		try {
			var bounds = Screen.PrimaryScreen?.Bounds;
			if(bounds == null) {
				AppLogger.Info("ScreenCaptureService.CapturePrimaryScreen no primary screen bounds");
				return null;
			}

			using var bmp = new Bitmap(bounds.Value.Width, bounds.Value.Height, PixelFormat.Format32bppArgb);
			using(var g = Graphics.FromImage(bmp)) {
				g.CopyFromScreen(bounds.Value.X, bounds.Value.Y, 0, 0, bounds.Value.Size, CopyPixelOperation.SourceCopy);
			}

			using var ms = new MemoryStream();
			bmp.Save(ms, ImageFormat.Png);
			ms.Position = 0;
			AppLogger.Info("ScreenCaptureService.CapturePrimaryScreen completed");
			return SKBitmap.Decode(ms);
		}
		catch(Exception ex) {
			AppLogger.Error("ScreenCaptureService.CapturePrimaryScreen failed", ex);
			return null;
		}
	}
}