namespace WinNsfwScan;

/// <summary>
/// Shared utility for filtering NSFW class names from NudeNet detection results.
/// </summary>
public static class NsfwClassifier {
	private const float GlobalMinScore = 0.625f;

	public static bool IsNsfwClass(string className) {
		return IsNsfwDetection(className, 1.0f);
	}

	public static bool IsNsfwDetection(string className, float score) {
		if(score < GlobalMinScore)
			return false;

		// Exclude face classes entirely
		if(className.StartsWith("FACE_", StringComparison.OrdinalIgnoreCase))
			return false;

		// Exclude feet classes entirely
		if(className.StartsWith("FEET_", StringComparison.OrdinalIgnoreCase))
			return false;

		// Exclude armpit classes entirely
		if(className.StartsWith("ARMPIT_", StringComparison.OrdinalIgnoreCase)
			|| className.StartsWith("ARMPITS_", StringComparison.OrdinalIgnoreCase))
			return false;

		// Exclude hand classes entirely
		if(className.StartsWith("HAND_", StringComparison.OrdinalIgnoreCase)
			|| className.StartsWith("HANDS_", StringComparison.OrdinalIgnoreCase))
			return false;

		// Exclude male classes except for male genitalia
		if(className.StartsWith("MALE_", StringComparison.OrdinalIgnoreCase)) {
			return className.Contains("GENITALIA", StringComparison.OrdinalIgnoreCase);
		}

		return true;
	}
}
