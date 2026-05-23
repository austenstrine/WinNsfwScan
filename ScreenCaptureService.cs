using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using static Vortice.Direct3D11.D3D11;

namespace WinNsfwScan;

public class ScreenCaptureService : IDisposable {
	private readonly object _sync = new();
	private readonly DxgiDesktopDuplicator? _dxgiDuplicator;
	private bool _dxgiDisabled;
	private bool _disposed;

	public ScreenCaptureService() {
		try {
			_dxgiDuplicator = new DxgiDesktopDuplicator();
			AppLogger.Info("ScreenCaptureService initialized captureBackend=dxgi");
		}
		catch(Exception ex) {
			_dxgiDisabled = true;
			AppLogger.Error("ScreenCaptureService DXGI init failed, falling back to GDI", ex);
		}
	}

	public SKBitmap? CapturePrimaryScreen(out string captureBackend) {
		captureBackend = "none";

		if(_disposed)
			return null;

		try {
			if(!_dxgiDisabled && _dxgiDuplicator != null) {
				lock(_sync) {
					var dxgiBitmap = _dxgiDuplicator.TryCapture();
					if(dxgiBitmap != null) {
						captureBackend = "dxgi";
						return dxgiBitmap;
					}
				}
			}

			var gdiBitmap = CaptureWithGdi();
			if(gdiBitmap != null)
				captureBackend = "gdi";

			return gdiBitmap;
		}
		catch(Exception ex) {
			AppLogger.Error("ScreenCaptureService.CapturePrimaryScreen failed", ex);
			return null;
		}
	}

	private SKBitmap? CaptureWithGdi() {
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
			AppLogger.Error("ScreenCaptureService.CaptureWithGdi failed", ex);
			return null;
		}
	}

	public void Dispose() {
		if(_disposed)
			return;

		lock(_sync) {
			if(_disposed)
				return;

			_disposed = true;
			_dxgiDuplicator?.Dispose();
		}
	}

	private sealed class DxgiDesktopDuplicator : IDisposable {
		private readonly ID3D11Device _device;
		private readonly ID3D11DeviceContext _context;
		private readonly IDXGIOutputDuplication _duplication;
		private ID3D11Texture2D? _stagingTexture;
		private int _stagingWidth;
		private int _stagingHeight;
		private bool _disposed;

		public DxgiDesktopDuplicator() {
			FeatureLevel[] featureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
			var createResult = D3D11CreateDevice(
				adapter: null,
				driverType: DriverType.Hardware,
				flags: DeviceCreationFlags.BgraSupport,
				featureLevels: featureLevels,
				device: out var device,
				featureLevel: out _,
				immediateContext: out var context
			);

			if(createResult.Failure)
				throw new InvalidOperationException($"D3D11CreateDevice failed: {createResult}");

			_device = device;
			_context = context;

			using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
			using var adapter = dxgiDevice.GetAdapter();
			adapter.EnumOutputs(0, out var output);
			using var output0 = output;
			using var output1 = output.QueryInterface<IDXGIOutput1>();
			_duplication = output1.DuplicateOutput(_device);
		}

		public SKBitmap? TryCapture() {
			if(_disposed)
				return null;

			IDXGIResource? desktopResource = null;
			bool frameAcquired = false;
			try {
				var acquireResult = _duplication.AcquireNextFrame(0, out _, out desktopResource);
				if(acquireResult == Vortice.DXGI.ResultCode.WaitTimeout)
					return null;

				if(acquireResult.Failure)
					throw new InvalidOperationException($"AcquireNextFrame failed: {acquireResult}");

				frameAcquired = true;

				using var desktopTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();
				Texture2DDescription desc = desktopTexture.Description;

				EnsureStagingTexture(desc.Width, desc.Height, desc.Format);
				_context.CopyResource(_stagingTexture!, desktopTexture);

				var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
				try {
					int width = (int)desc.Width;
					int height = (int)desc.Height;
					var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
					unsafe {
						byte* srcBase = (byte*)mapped.DataPointer;
						byte* dstBase = (byte*)skBitmap.GetPixels();
						for(int y = 0; y < height; y++) {
							Buffer.MemoryCopy(
								srcBase + (y * mapped.RowPitch),
								dstBase + (y * skBitmap.RowBytes),
								skBitmap.RowBytes,
								skBitmap.RowBytes
							);
						}
					}

					return skBitmap;
				}
				finally {
					_context.Unmap(_stagingTexture!, 0);
				}
			}
			finally {
				if(desktopResource != null)
					desktopResource.Dispose();

				if(frameAcquired) {
					try { _duplication.ReleaseFrame(); } catch { }
				}
			}
		}

		private void EnsureStagingTexture(uint width, uint height, Format format) {
			if(_stagingTexture != null && _stagingWidth == (int)width && _stagingHeight == (int)height)
				return;

			_stagingTexture?.Dispose();
			_stagingTexture = _device.CreateTexture2D(new Texture2DDescription {
				Width = width,
				Height = height,
				MipLevels = 1,
				ArraySize = 1,
				Format = format,
				SampleDescription = new SampleDescription(1, 0),
				Usage = ResourceUsage.Staging,
				BindFlags = BindFlags.None,
				CPUAccessFlags = CpuAccessFlags.Read,
				MiscFlags = ResourceOptionFlags.None
			});

			_stagingWidth = (int)width;
			_stagingHeight = (int)height;
		}

		public void Dispose() {
			if(_disposed)
				return;

			_disposed = true;
			_stagingTexture?.Dispose();
			_duplication.Dispose();
			_context.Dispose();
			_device.Dispose();
		}
	}
}