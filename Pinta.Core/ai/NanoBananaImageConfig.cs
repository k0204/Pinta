using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Pinta.Core;

namespace Pinta.Core.AI;

internal sealed record NanoBananaImageOption (string Resolution, string AspectRatio, Size Size);

public static class NanoBananaImageConfig
{
	private const string config_file = "NanoBanana.json";
	private static readonly string[] model_keys = ["nano-banana-pro", "nano-banana"];
	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNameCaseInsensitive = true,
	};

	public static IReadOnlyList<Size> GetImageGenerationSizes ()
		=> [.. GetImageGenerationOptions ().Select (option => option.Size).Distinct ()];

	internal static IReadOnlyList<NanoBananaImageOption> GetImageGenerationOptions ()
	{
		NanoBananaConfig config = ReadConfig ();
		string[] tiers = FindModelTiers (config);
		List<NanoBananaImageOption> options = [];

		foreach (string tier in tiers) {
			if (!config.ResolutionMap.TryGetValue (tier, out Dictionary<string, string>? ratios))
				throw new InvalidOperationException ($"Nano Banana config is missing resolution tier: {tier}");

			foreach ((string ratio, string value) in ratios)
				options.Add (new (tier, ratio, ParseSize (value, tier, ratio)));
		}

		return options.Count > 0
			? options
			: throw new InvalidOperationException ($"Nano Banana config has no resolutions: {GetConfigPath ()}");
	}

	internal static NanoBananaImageOption? FindImageGenerationOption (Size size)
		=> GetImageGenerationOptions ().FirstOrDefault (option => option.Size == size);

	private static NanoBananaConfig ReadConfig ()
	{
		string path = GetConfigPath ();
		if (!File.Exists (path))
			throw new FileNotFoundException ($"Nano Banana config was not found: {path}", path);

		NanoBananaConfig config = JsonSerializer.Deserialize<NanoBananaConfig> (
			File.ReadAllText (path),
			json_options)
			?? throw new InvalidOperationException ($"Nano Banana config is empty: {path}");
		return config;
	}

	private static string[] FindModelTiers (NanoBananaConfig config)
	{
		foreach (string modelKey in model_keys)
			if (config.ModelTierRules.TryGetValue (modelKey, out string[]? tiers) && tiers.Length > 0)
				return tiers;

		throw new InvalidOperationException ($"Nano Banana config has no supported model tiers: {GetConfigPath ()}");
	}

	private static Size ParseSize (string value, string tier, string ratio)
	{
		string[] parts = value.Split ('x', StringSplitOptions.TrimEntries);
		if (parts.Length != 2 ||
			!int.TryParse (parts[0], out int width) ||
			!int.TryParse (parts[1], out int height) ||
			width <= 0 ||
			height <= 0)
			throw new InvalidOperationException ($"Invalid Nano Banana resolution '{value}' for {tier} {ratio}.");

		return new Size (width, height);
	}

	private static string GetConfigPath ()
		=> Path.Combine (AppContext.BaseDirectory, "config", config_file);

	private sealed class NanoBananaConfig
	{
		[JsonPropertyName ("resolution_map")]
		public Dictionary<string, Dictionary<string, string>> ResolutionMap { get; init; } = [];

		[JsonPropertyName ("model_tier_rules")]
		public Dictionary<string, string[]> ModelTierRules { get; init; } = [];
	}
}
