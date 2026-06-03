namespace WinNsfwScan;

/// <summary>
/// A single NSFW detection result.
/// Box values are in source-image pixel coordinates: X/Y is top-left, Width/Height are dimensions.
/// </summary>
public record NsfwDetection(string Class, float Score, int X, int Y, int Width, int Height);
