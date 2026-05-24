namespace WinNsfwScan;

/// <summary>
/// Shared utility for filtering NSFW class names from NudeNet detection results.
/// </summary>
public static class NsfwClassifier {
	private const float GlobalMinScore = 0.05f;
	private const float HardMinScore = 0.70f;

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

	public static bool IsHardNsfwDetection(string className, float score) {
		if(score < HardMinScore)
			return false;

		if(className.Contains("GENITALIA", StringComparison.OrdinalIgnoreCase))
			return true;

		if(string.Equals(className, "FEMALE_BREAST_EXPOSED", StringComparison.OrdinalIgnoreCase))
			return true;

		if(className.Contains("BUTTOCKS_EXPOSED", StringComparison.OrdinalIgnoreCase))
			return true;

		return false;
	}
}
