namespace WinNsfwScan;

/// <summary>
/// Shared utility for filtering NSFW class names from NudeNet detection results.
/// </summary>
public static class NsfwClassifier {
	private const float ButtocksCoveredMinScore = 0.30f;

	public static bool IsNsfwClass(string className) {
		return IsNsfwDetection(className, 1.0f);
	}

	public static bool IsNsfwDetection(string className, float score) {
		// Exclude face classes entirely
		if(className.StartsWith("FACE_", StringComparison.OrdinalIgnoreCase))
			return false;

		// Exclude feet classes entirely
		if(className.StartsWith("FEET_", StringComparison.OrdinalIgnoreCase))
			return false;

		// Exclude male classes except for male genitalia
		if(className.StartsWith("MALE_", StringComparison.OrdinalIgnoreCase)) {
			return className.Contains("GENITALIA", StringComparison.OrdinalIgnoreCase);
		}

		// Treat buttocks covered as NSFW only above a confidence floor.
		if(className.Equals("BUTTOCKS_COVERED", StringComparison.OrdinalIgnoreCase)) {
			return score >= ButtocksCoveredMinScore;
		}

		return true;
	}
}
