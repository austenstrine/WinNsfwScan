namespace WinNsfwScan;

using System.Text;

/// <summary>
/// Shared utility for filtering and labelling mixed EraX + NudeNet detection results.
/// </summary>
public static class NsfwClassifier {
	public static float GlobalMinScore = 0.05f;
	public static float HardMinScore = 0.70f;

	public static bool IsNsfwClass(string className) {
		return IsNsfwDetection(className, 1.0f);
	}

	public static bool IsNsfwClass(string source, string className) {
		return IsNsfwDetection(source, className, 1.0f);
	}

	/// <summary>All EraX detections above the score threshold are NSFW.</summary>
	public static bool IsNsfwDetection(string className, float score) {
		return IsNsfwDetection("erax", className, score);
	}

	/// <summary>
	/// Source-aware NSFW classification.
	/// EraX and NsfwSharp classes are explicit-only. NudeNet includes a broader set of sexual classes,
	/// while hard blocks remain explicit-only.
	/// </summary>
	public static bool IsNsfwDetection(string source, string className, float score) {
		if(score < GlobalMinScore)
			return false;

		if(source.Equals("nudenet", StringComparison.OrdinalIgnoreCase))
			return IsNudeNetNsfwDetection(className);

		return score >= GlobalMinScore;
	}

	/// <summary>All EraX classes are explicit enough to warrant hard-blocking above the hard threshold.</summary>
	public static bool IsHardNsfwDetection(string className, float score) {
		return IsHardNsfwDetection("erax", className, score);
	}

	public static bool IsHardNsfwDetection(string source, string className, float score) {
		if(score < HardMinScore)
			return false;

		if(source.Equals("nudenet", StringComparison.OrdinalIgnoreCase))
			return IsNudeNetHardNsfwDetection(className);

		// ERAx and NsfwSharp: all classes above hard threshold qualify.
		return score >= HardMinScore;
	}

	/// <summary>
	/// Matches the original NudeNet exclusion strategy:
	/// exclude face, feet, armpits, hands, and non-genitalia male classes; accept everything else.
	/// </summary>
	private static bool IsNudeNetNsfwDetection(string className) {
		if(className.StartsWith("FACE_", StringComparison.OrdinalIgnoreCase))
			return false;

		if(className.StartsWith("FEET_", StringComparison.OrdinalIgnoreCase))
			return false;

		if(className.StartsWith("ARMPIT_", StringComparison.OrdinalIgnoreCase)
			|| className.StartsWith("ARMPITS_", StringComparison.OrdinalIgnoreCase))
			return false;

		if(className.StartsWith("HAND_", StringComparison.OrdinalIgnoreCase)
			|| className.StartsWith("HANDS_", StringComparison.OrdinalIgnoreCase))
			return false;

		if(className.StartsWith("MALE_", StringComparison.OrdinalIgnoreCase))
			return className.Contains("GENITALIA", StringComparison.OrdinalIgnoreCase);

		return true;
	}

	/// <summary>
	/// Hard-block NudeNet classes: any genitalia, exposed female breast, or exposed buttocks.
	/// </summary>
	private static bool IsNudeNetHardNsfwDetection(string className) {
		if(className.Contains("GENITALIA", StringComparison.OrdinalIgnoreCase))
			return true;

		if(string.Equals(className, "FEMALE_BREAST_EXPOSED", StringComparison.OrdinalIgnoreCase))
			return true;

		if(className.Contains("BUTTOCKS_EXPOSED", StringComparison.OrdinalIgnoreCase))
			return true;

		return false;
	}

	public static string GetClassShortCode(string className) {
		if(string.IsNullOrWhiteSpace(className))
			return "NSFW";

		string[] parts = className.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if(parts.Length == 0)
			return "NSFW";

		var sb = new StringBuilder(3);
		foreach(string part in parts) {
			if(part.Length == 0) continue;
			sb.Append(char.ToUpperInvariant(part[0]));
			if(sb.Length >= 3) break;
		}

		return sb.Length > 0 ? sb.ToString() : "NSFW";
	}
}
