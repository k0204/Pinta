using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Pinta.Core.AI;

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
	private const string generation_prompt_file = "direction-sheet.txt";
	public const string FixedBackgroundId = "white";
	private static readonly string[] direction_ids = [
		"down", "down-right", "right", "up-right", "up", "up-left", "left", "down-left",
	];
	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNameCaseInsensitive = true,
	};

	private readonly string generation_prompt;

	private SpritesheetPromptCatalog (
		string generationPrompt,
		IReadOnlyList<SpritesheetActionPreset> actions)
	{
		generation_prompt = generationPrompt;
		Actions = actions;
	}

	public IReadOnlyList<string> DirectionIds => direction_ids;
	public IReadOnlyList<SpritesheetActionPreset> Actions { get; }

	public static SpritesheetPromptCatalog Load ()
	{
		string root = Path.Combine (AppContext.BaseDirectory, "config", config_directory);
		string generationPrompt = PromptFileReader.ReadRequired (
			Path.Combine (root, generation_prompt_file),
			"Spritesheet prompt");
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

		Validate (generationPrompt, actions);
		return new (generationPrompt, actions);
	}

	public string BuildPrompt (
		bool directionSheet,
		string actionId,
		string customAction,
		int frameCount,
		Size imageSize)
	{
		int framesPerDirection = directionSheet ? 1 : Math.Clamp (frameCount, 1, 16);
		int totalFrames = direction_ids.Length * framesPerDirection;
		(int columns, int rows) = CalculateGrid (totalFrames, imageSize);

		List<string> sections = [generation_prompt];
		if (!directionSheet)
			sections.Add (BuildActionPrompt (actionId, customAction, framesPerDirection));

		sections.Add (directionSheet
			? $"生成 {direction_ids.Length} 个标准方向视图。\n每个方向 1 帧。"
			: $"每个固定方向严格使用 {framesPerDirection} 帧。\n动画总帧数：{totalFrames}。"
			+ "\n同一帧序号下，所有固定方向必须表现相同的动画阶段，只改变观察方向。\n"
			+ "各方向的时序、接触状态、身体力学、手持物和动作强度必须一致。");

		sections.Add (BuildDirectionCellPlan (framesPerDirection));
		int unusedCells = columns * rows - totalFrames;
		sections.Add (
			$"输出一张 {imageSize.Width}x{imageSize.Height} 的光栅精灵图，排列为 {columns} 列 x {rows} 行。\n"
			+ "从左上角的 1 号单元格开始编号，先从左到右，再从上到下。\n"
			+ "所有单元格尺寸必须完全相同。"
			+ (unusedCells > 0 ? $"\n最后 {unusedCells} 个未使用的单元格除纯白背景外必须完全留空。" : string.Empty));
		return string.Join ($"{Environment.NewLine}{Environment.NewLine}", sections.Where (section => !string.IsNullOrWhiteSpace (section)));
	}

	private static string BuildDirectionCellPlan (int framesPerDirection)
	{
		List<string> lines = [];
		int firstCell = 1;
		foreach (string directionId in direction_ids) {
			int lastCell = firstCell + framesPerDirection - 1;
			string cells = firstCell == lastCell ? $"单元格 {firstCell}" : $"单元格 {firstCell}-{lastCell}";
			lines.Add ($"- {cells}：{directionId}");
			firstCell = lastCell + 1;
		}
		string layout = framesPerDirection == 1
			? "方向图固定使用 4 列 x 2 行：第一行是单元格 1-4，第二行是单元格 5-8。"
			: "每个方向的帧必须保持连续，并按方向顺序分组。";
		return $"方向与单元格分配（固定顺时针顺序，禁止重排）：\n{layout}\n{string.Join (Environment.NewLine, lines)}";
	}

	public static (int Columns, int Rows) CalculateGrid (int frameCount, Size imageSize)
	{
		if (frameCount < 1)
			throw new ArgumentOutOfRangeException (nameof (frameCount));
		if (frameCount == direction_ids.Length)
			return (4, 2);

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
		return $"所有固定方向统一使用以下固定帧计划：\n{string.Join (Environment.NewLine, frames)}";
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
		string generationPrompt,
		IReadOnlyList<SpritesheetActionPreset> actions)
	{
		if (direction_ids.Length != 8 || direction_ids.Distinct ().Count () != direction_ids.Length)
			throw new InvalidOperationException ("Spritesheet prompts must define exactly eight unique directions.");
		if (direction_ids.Any (direction => !generationPrompt.Contains (direction, StringComparison.OrdinalIgnoreCase)))
			throw new InvalidOperationException ("Spritesheet prompt must define all eight fixed directions.");
		if (!generationPrompt.Contains ("#FFFFFF", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException ("Spritesheet prompt must define a white background.");
		if (actions.Count != 8 || actions.Any (item => item.DefaultFrameCount is < 1 or > 16
			|| string.IsNullOrWhiteSpace (item.Prompt)
			|| item.KeyPoses.Count == 0
			|| item.KeyPoses.Any (string.IsNullOrWhiteSpace)))
			throw new InvalidOperationException ("Spritesheet prompts must define seven actions and one custom action.");
		if (actions.Select (item => item.Id).Distinct ().Count () != actions.Count)
			throw new InvalidOperationException ("Spritesheet prompt IDs must be unique.");
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
