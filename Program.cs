class Program {
	static async Task Main(string[] args) {
		if (args.Length == 0) {
			Console.WriteLine("Usage: dotnet run <image-path>");
			return;
		}

		string imagePath = args[0];

		using var detector = new NudeNetClient();

		bool isNsfw = await detector.IsNsfwAsync(imagePath);
		Console.WriteLine(isNsfw ? "NSFW: YES" : "NSFW: NO");
	}
}