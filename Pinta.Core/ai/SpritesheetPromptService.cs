using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Pinta.Core.AI;

public sealed record SpritesheetDirection (string Id, string Label, string Prompt);

public sealed record SpritesheetBackground (string Id, string Label, string Prompt);

public sealed record SpritesheetActionPreset (
	string Id,
	string Label,
	int DefaultFrameCount,
	int Order,
	string Prompt);

public sealed class SpritesheetPromptCatalog
{
	private const string config_directory = "spritesheet-prompts";
	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNameCaseInsensitive = true,
	};

	private readonly CommonPromptConfig common;
	private readonly string direction_prompt;

	private SpritesheetPromptCatalog (
		CommonPromptConfig common,
		string directionPrompt,
		IReadOnlyList<SpritesheetActionPreset> actions)
	{
		this.common = common;
		direction_prompt = directionPrompt;
		Directions = common.Directions;
		Backgrounds = common.Backgrounds;
		Actions = actions;
	}

	public IReadOnlyList<SpritesheetDirection> Directions { get; }
	public IReadOnlyList<SpritesheetBackground> Backgrounds { get; }
	public IReadOnlyList<SpritesheetActionPreset> Actions { get; }

	public static SpritesheetPromptCatalog Load ()
	{
		string root = Path.Combine (AppContext.BaseDirectory, "config", config_directory);
		CommonPromptConfig common = ReadJson<CommonPromptConfig> (Path.Combine (root, "common.json"));
		DirectionPromptConfig direction = ReadJson<DirectionPromptConfig> (Path.Combine (root, "direction-sheet.json"));
		string actionsDirectory = Path.Combine (root, "actions");
		if (!Directory.Exists (actionsDirectory))
			throw new InvalidOperationException ($"Spritesheet action prompt directory was not found: {actionsDirectory}");

		SpritesheetActionPreset[] actions = Directory.GetFiles (actionsDirectory, "*.json")
			.Select (ReadJson<ActionPromptConfig>)
			.Select (action => new SpritesheetActionPreset (
				action.Id,
				action.Label,
				action.DefaultFrameCount,
				action.Order,
				action.Prompt))
			.OrderBy (action => action.Order)
			.ToArray ();

		Validate (common, direction, actions);
		return new (common, direction.Prompt, actions);
	}

	public string BuildPrompt (
		bool directionSheet,
		string actionId,
		string customAction,
		IReadOnlyCollection<string> selectedDirectionIds,
		int frameCount,
		string backgroundId,
		Size imageSize)
	{
		SpritesheetDirection[] selectedDirections = Directions
			.Where (direction => selectedDirectionIds.Contains (direction.Id))
			.ToArray ();
		if (selectedDirections.Length == 0)
			return string.Empty;

		int framesPerDirection = directionSheet ? 1 : Math.Clamp (frameCount, 1, 16);
		int totalFrames = selectedDirections.Length * framesPerDirection;
		(int columns, int rows) = CalculateGrid (totalFrames, imageSize);
		SpritesheetBackground background = Backgrounds.FirstOrDefault (item => item.Id == backgroundId)
			?? throw new InvalidOperationException ($"Unknown spritesheet background: {backgroundId}");

		List<string> sections = [directionSheet
			? direction_prompt
			: BuildActionPrompt (actionId, customAction)];

		sections.Add (directionSheet
			? $"Generate {selectedDirections.Length} canonical direction views, one frame per direction."
			: $"Use exactly {framesPerDirection} frames for every selected direction. Total animation frames: {totalFrames}.");

		List<string> directionLines = [];
		int firstCell = 1;
		foreach (SpritesheetDirection direction in selectedDirections) {
			int lastCell = firstCell + framesPerDirection - 1;
			string cells = firstCell == lastCell ? $"cell {firstCell}" : $"cells {firstCell}-{lastCell}";
			directionLines.Add ($"- {cells}: {direction.Label}. {direction.Prompt}");
			firstCell = lastCell + 1;
		}
		sections.Add ($"Direction and cell assignment (fixed row-major order):\n{string.Join (Environment.NewLine, directionLines)}");

		int unusedCells = columns * rows - totalFrames;
		sections.Add ($"Output one {imageSize.Width}x{imageSize.Height} raster spritesheet arranged as {columns} columns x {rows} rows. Number cells from 1 at the top-left, moving left-to-right and then top-to-bottom. Every cell must have identical dimensions."
			+ (unusedCells > 0 ? $" Leave the final {unusedCells} unused cell(s) completely empty except for the selected background." : string.Empty));
		sections.Add (common.SharedRules);
		sections.Add (background.Prompt);
		sections.Add (common.ForbiddenContent);
		return string.Join ($"{Environment.NewLine}{Environment.NewLine}", sections.Where (section => !string.IsNullOrWhiteSpace (section)));
	}

	public static (int Columns, int Rows) CalculateGrid (int frameCount, Size imageSize)
	{
		if (frameCount < 1)
			throw new ArgumentOutOfRangeException (nameof (frameCount));

		double targetRatio = imageSize.Width / (double) imageSize.Height;
		int bestColumns = 1;
		int bestRows = frameCount;
		double bestError = double.MaxValue;
		int bestUnused = int.MaxValue;
		for (int columns = 1; columns <= frameCount; columns++) {
			int rows = (frameCount + columns - 1) / columns;
			double error = Math.Abs (Math.Log ((columns / (double) rows) / targetRatio));
			int unused = columns * rows - frameCount;
			if (error < bestError - 0.000001 || (Math.Abs (error - bestError) < 0.000001 && unused < bestUnused)) {
				bestColumns = columns;
				bestRows = rows;
				bestError = error;
				bestUnused = unused;
			}
		}
		return (bestColumns, bestRows);
	}

	private string BuildActionPrompt (string actionId, string customAction)
	{
		SpritesheetActionPreset action = Actions.FirstOrDefault (item => item.Id == actionId)
			?? throw new InvalidOperationException ($"Unknown spritesheet action: {actionId}");
		if (action.Id != "custom")
			return action.Prompt;
		if (string.IsNullOrWhiteSpace (customAction))
			return string.Empty;
		return $"{action.Prompt}\nCustom action: {customAction.Trim ()}";
	}

	private static T ReadJson<T> (string path)
	{
		if (!File.Exists (path))
			throw new InvalidOperationException ($"Spritesheet prompt file was not found: {path}");
		try {
			return JsonSerializer.Deserialize<T> (File.ReadAllText (path), json_options)
				?? throw new InvalidOperationException ($"Spritesheet prompt file is empty: {path}");
		} catch (JsonException ex) {
			throw new InvalidOperationException ($"Invalid spritesheet prompt file: {path}", ex);
		}
	}

	private static void Validate (
		CommonPromptConfig common,
		DirectionPromptConfig direction,
		IReadOnlyList<SpritesheetActionPreset> actions)
	{
		if (string.IsNullOrWhiteSpace (common.SharedRules) || string.IsNullOrWhiteSpace (common.ForbiddenContent))
			throw new InvalidOperationException ("Spritesheet common prompt rules cannot be empty.");
		if (common.Directions.Count != 8 || common.Directions.Any (item => string.IsNullOrWhiteSpace (item.Id) || string.IsNullOrWhiteSpace (item.Prompt)))
			throw new InvalidOperationException ("Spritesheet prompts must define exactly eight valid directions.");
		if (common.Backgrounds.Count != 3 || common.Backgrounds.All (item => item.Id != "white"))
			throw new InvalidOperationException ("Spritesheet prompts must define white, magenta, and green backgrounds.");
		if (string.IsNullOrWhiteSpace (direction.Prompt))
			throw new InvalidOperationException ("Direction sheet prompt cannot be empty.");
		if (actions.Count != 8 || actions.Any (item => item.DefaultFrameCount is < 1 or > 16 || string.IsNullOrWhiteSpace (item.Prompt)))
			throw new InvalidOperationException ("Spritesheet prompts must define seven actions and one custom action.");
		if (common.Directions.Select (item => item.Id).Distinct ().Count () != common.Directions.Count ||
			actions.Select (item => item.Id).Distinct ().Count () != actions.Count)
			throw new InvalidOperationException ("Spritesheet prompt IDs must be unique.");
	}

	private sealed class CommonPromptConfig
	{
		public string SharedRules { get; set; } = string.Empty;
		public string ForbiddenContent { get; set; } = string.Empty;
		public List<SpritesheetDirection> Directions { get; set; } = [];
		public List<SpritesheetBackground> Backgrounds { get; set; } = [];
	}

	private sealed class DirectionPromptConfig
	{
		public string Prompt { get; set; } = string.Empty;
	}

	private sealed class ActionPromptConfig
	{
		public string Id { get; set; } = string.Empty;
		public string Label { get; set; } = string.Empty;
		public int DefaultFrameCount { get; set; }
		public int Order { get; set; }
		public string Prompt { get; set; } = string.Empty;
	}
}
