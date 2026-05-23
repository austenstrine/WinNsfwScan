using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SkiaSharp;

namespace WinNsfwScan;

public class ScreenCaptureService {
	public SKBitmap? CapturePrimaryScreen() {
		try {
			var bounds = Screen.PrimaryScreen?.Bounds;
			if(bounds == null)
				return null;

			int w = bounds.Value.Width;
			int h = bounds.Value.Height;

			using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
			using (var g = Graphics.FromImage(bmp)) {
				g.CopyFromScreen(bounds.Value.X, bounds.Value.Y, 0, 0, bounds.Value.Size, CopyPixelOperation.SourceCopy);
			}

			// Copy pixels directly into SKBitmap — avoids the PNG encode/decode round-trip.
			// GDI Format32bppArgb and SKColorType.Bgra8888 are both BGRA in memory on x86/x64.
			var skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
			var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			try {
				unsafe {
					Buffer.MemoryCopy(
						(void*)bmpData.Scan0,
						(void*)skBitmap.GetPixels(),
						(long)h * skBitmap.RowBytes,
						(long)h * bmpData.Stride);
				}
			} finally {
				bmp.UnlockBits(bmpData);
			}

			return skBitmap;
		}
		catch(Exception ex) {
			AppLogger.Error("ScreenCaptureService.CapturePrimaryScreen failed", ex);
			return null;
		}
	}
}