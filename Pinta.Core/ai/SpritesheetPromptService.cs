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
	string Prompt,
	bool Loop,
	IReadOnlyList<string> KeyPoses);

public sealed record SpritesheetAttemptInfo (
	bool DirectionSheet,
	string ActionId,
	IReadOnlyList<string> DirectionIds,
	int FrameCount,
	int Columns,
	int Rows,
	string BackgroundId,
	Size ImageSize,
	string Prompt,
	string ImageService,
	string Provider,
	int PromptVersion);

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
		string directionPrompt = ReadPrompt (Path.Combine (root, "direction-sheet.txt"));
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
				action.Prompt,
				action.Loop,
				action.KeyPoses))
			.OrderBy (action => action.Order)
			.ToArray ();

		Validate (common, directionPrompt, actions);
		return new (common, directionPrompt, actions);
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
			: BuildActionPrompt (actionId, customAction, framesPerDirection)];

		sections.Add (directionSheet
			? $"生成 {selectedDirections.Length} 个标准方向视图。\n每个方向 1 帧。"
			: $"每个选中方向严格使用 {framesPerDirection} 帧。\n动画总帧数：{totalFrames}。");
		if (!directionSheet)
			sections.Add (
				"同一帧序号下，所有选中方向必须表现相同的标准化动画阶段和语义关键姿势。\n"
				+ "只改变观察方向。\n"
				+ "各方向的时序、接触状态、身体力学、手持物和动作强度必须一致。");

		List<string> directionLines = [];
		int firstCell = 1;
		foreach (SpritesheetDirection direction in selectedDirections) {
			int lastCell = firstCell + framesPerDirection - 1;
			string cells = firstCell == lastCell ? $"单元格 {firstCell}" : $"单元格 {firstCell}-{lastCell}";
			directionLines.Add ($"- {cells}：{direction.Label}\n  {direction.Prompt}");
			firstCell = lastCell + 1;
		}
		sections.Add ($"方向与单元格分配（固定行优先顺序）：\n{string.Join (Environment.NewLine, directionLines)}");

		int unusedCells = columns * rows - totalFrames;
		sections.Add (
			$"输出一张 {imageSize.Width}x{imageSize.Height} 的光栅精灵图，排列为 {columns} 列 x {rows} 行。\n"
			+ "从左上角的 1 号单元格开始编号，先从左到右，再从上到下。\n"
			+ "所有单元格尺寸必须完全相同。"
			+ (unusedCells > 0 ? $"\n最后 {unusedCells} 个未使用的单元格除选定背景外必须完全留空。" : string.Empty));
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

	private string BuildActionPrompt (string actionId, string customAction, int frameCount)
	{
		SpritesheetActionPreset action = Actions.FirstOrDefault (item => item.Id == actionId)
			?? throw new InvalidOperationException ($"Unknown spritesheet action: {actionId}");
		string prompt = action.Id == "custom"
			? $"{action.Prompt}\n自定义动作：{customAction.Trim ()}"
			: action.Prompt;
		return $"{prompt}\n{BuildFramePlan (action, frameCount)}";
	}

	private static string BuildFramePlan (SpritesheetActionPreset action, int frameCount)
	{
		List<string> frames = [];
		for (int frame = 0; frame < frameCount; frame++) {
			double phase = action.Loop
				? frame / (double) frameCount
				: frameCount == 1 ? 0.5 : frame / (double) (frameCount - 1);
			int pose = action.Loop
				? Math.Min ((int) (phase * action.KeyPoses.Count), action.KeyPoses.Count - 1)
				: (int) Math.Round (phase * (action.KeyPoses.Count - 1));
			frames.Add ($"- 第 {frame + 1} 帧：阶段 {phase:0.###}；{action.KeyPoses[pose]}");
		}
		return $"所有方向统一使用以下固定帧计划：\n{string.Join (Environment.NewLine, frames)}";
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

	private static string ReadPrompt (string path)
	{
		if (!File.Exists (path))
			throw new InvalidOperationException ($"Spritesheet prompt file was not found: {path}");
		string prompt = File.ReadAllText (path).Trim ();
		if (string.IsNullOrWhiteSpace (prompt))
			throw new InvalidOperationException ($"Spritesheet prompt file is empty: {path}");
		return prompt;
	}

	private static void Validate (
		CommonPromptConfig common,
		string directionPrompt,
		IReadOnlyList<SpritesheetActionPreset> actions)
	{
		if (string.IsNullOrWhiteSpace (common.SharedRules) || string.IsNullOrWhiteSpace (common.ForbiddenContent))
			throw new InvalidOperationException ("Spritesheet common prompt rules cannot be empty.");
		if (common.Directions.Count != 8 || common.Directions.Any (item => string.IsNullOrWhiteSpace (item.Id) || string.IsNullOrWhiteSpace (item.Prompt)))
			throw new InvalidOperationException ("Spritesheet prompts must define exactly eight valid directions.");
		if (common.Backgrounds.Count != 3 || common.Backgrounds.All (item => item.Id != "white"))
			throw new InvalidOperationException ("Spritesheet prompts must define white, magenta, and green backgrounds.");
		if (string.IsNullOrWhiteSpace (directionPrompt))
			throw new InvalidOperationException ("Direction sheet prompt cannot be empty.");
		if (actions.Count != 8 || actions.Any (item => item.DefaultFrameCount is < 1 or > 16
			|| string.IsNullOrWhiteSpace (item.Prompt)
			|| item.KeyPoses.Count == 0
			|| item.KeyPoses.Any (string.IsNullOrWhiteSpace)))
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

	private sealed class ActionPromptConfig
	{
		public string Id { get; set; } = string.Empty;
		public string Label { get; set; } = string.Empty;
		public int DefaultFrameCount { get; set; }
		public int Order { get; set; }
		public string Prompt { get; set; } = string.Empty;
		public bool Loop { get; set; }
		public List<string> KeyPoses { get; set; } = [];
	}
}
