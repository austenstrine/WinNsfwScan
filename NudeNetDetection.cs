namespace WinNsfwScan;

/// <summary>
/// A single detection result from NudeNet.
/// Box values are in source-image pixel coordinates: X/Y is top-left, Width/Height are dimensions.
/// </summary>
public record NudeNetDetection(string Class, float Score, int X, int Y, int Width, int Height);
