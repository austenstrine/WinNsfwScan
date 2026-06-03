namespace WinNsfwScan;

using System.Text;

/// <summary>
/// Shared utility for filtering and labelling EraX NSFW detection results.
/// EraX classes: anus, make_love, nipple, penis, vagina — all are explicit NSFW.
/// </summary>
public static class NsfwClassifier {
	public static float GlobalMinScore = 0.25f;
	public static float HardMinScore = 0.50f;

	public static bool IsNsfwClass(string className) {
		return IsNsfwDetection(className, 1.0f);
	}

	/// <summary>All EraX detections above the score threshold are NSFW.</summary>
	public static bool IsNsfwDetection(string className, float score) {
		return score >= GlobalMinScore;
	}

	/// <summary>All EraX classes are explicit enough to warrant hard-blocking above the hard threshold.</summary>
	public static bool IsHardNsfwDetection(string className, float score) {
		return score >= HardMinScore;
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
