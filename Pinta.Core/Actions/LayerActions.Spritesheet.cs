using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private const string spritesheet_attempt_metadata = "pinta.spritesheet.attempt";
	private const string spritesheet_split_metadata = "pinta.spritesheet.split";
	private const string spritesheet_anchor_metadata = "pinta.spritesheet.character-anchor";

	private async void HandlePintaCoreActionsLayersGenerateSpritesheetActivated (object sender, EventArgs e)
	{
		if (cutout_running || !EnsureAiLoggedIn () || workspace.ActiveDocumentOrDefault is not Document document)
			return;

		try {
			AiImageRequestOptions? options = await PromptAiImageRequestAsync (
				AiImageRequestMode.SpritesheetGeneration,
				document,
				sourceLayer: null);
			if (options is not null)
				await GenerateImageAsync (document, options);
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Spritesheet Prompt Configuration Error"),
				Translations.GetString ("Check the files in config/spritesheet-prompts and try again."),
				ex.ToString ());
		}
	}

	private async void HandlePintaCoreActionsLayersSplitSpritesheetActivated (object sender, EventArgs e)
	{
		Document document = workspace.ActiveDocument;
		UserLayer source = document.Layers.CurrentUserLayer;
		if (!TryGetSpritesheetAttempt (source, out UserLayer? attempt, out AI.SpritesheetAttemptInfo? info)
			|| attempt is null
			|| info is null)
			return;

		SpritesheetSplitOptions? split = await PromptSpritesheetSplitAsync (source, info);
		if (split is null)
			return;

		tools.Commit ();
		ApplySpritesheetSplit (document, attempt, source, info, split);
	}

	private void HandleSetSpritesheetAnchorActivated (object sender, EventArgs e)
	{
		Document document = workspace.ActiveDocument;
		UserLayer source = document.Layers.CurrentUserLayer;
		if (!IsDirectionSheetSource (source))
			return;

		CompoundHistoryItem history = new (Resources.Icons.LayerDuplicate, Translations.GetString ("Set Character Anchor"));
		foreach (UserLayer layer in document.Layers.AllLayers.Where (IsDirectionSheetSource)) {
			string? value = layer == source ? "true" : null;
			history.Push (new SpritesheetMetadataHistoryItem (layer, spritesheet_anchor_metadata, value));
			SetMetadata (layer, spritesheet_anchor_metadata, value);
		}
		document.History.PushNewItem (history);
	}

	private void InsertSpritesheetAttempt (
		Document document,
		byte[] png,
		AI.SpritesheetAttemptInfo info)
	{
		tools.Commit ();
		CompoundHistoryItem history = new (Resources.Icons.LayerDuplicate, Translations.GetString ("Generate Spritesheet"));
		GroupLayer root = FindOrCreateGroup (document, null, "spritesheet", history);
		GroupLayer action = FindOrCreateGroup (document, root, info.ActionId, history);
		GroupLayer attempt = document.Layers.CreateGroupLayer (GetNextAttemptName (action));
		document.Layers.Insert (attempt, new LayerPosition (action, action.Children.Count));
		history.Push (CreateAddHistory (document, attempt));

		UserLayer source = document.Layers.CreateLayer ("source-sheet", info.ImageSize.Width, info.ImageSize.Height);
		DrawPngOnLayer (png, source);
		string json = JsonSerializer.Serialize (info);
		attempt.Metadata.Add (spritesheet_attempt_metadata, json);
		source.Metadata.Add (spritesheet_attempt_metadata, json);
		document.Layers.Insert (source, new LayerPosition (attempt, 0));
		history.Push (CreateAddHistory (document, source));
		document.Layers.SetCurrentUserLayer (source);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
	}

	private GroupLayer FindOrCreateGroup (
		Document document,
		UserLayer? parent,
		string name,
		CompoundHistoryItem history)
	{
		IReadOnlyList<UserLayer> children = parent?.Children ?? document.Layers.RootLayers;
		if (children.FirstOrDefault (layer => layer is GroupLayer && layer.Name == name) is GroupLayer existing)
			return existing;

		GroupLayer group = document.Layers.CreateGroupLayer (name);
		document.Layers.Insert (group, new LayerPosition (parent, children.Count));
		history.Push (CreateAddHistory (document, group));
		return group;
	}

	private static AddLayerHistoryItem CreateAddHistory (Document document, UserLayer layer)
		=> new (string.Empty, string.Empty, layer, document.Layers.GetPosition (layer));

	private static string GetNextAttemptName (UserLayer action)
	{
		int next = 1;
		while (action.Children.Any (child => child.Name == $"attempt-{next:D2}"))
			next++;
		return $"attempt-{next:D2}";
	}

	private static bool IsSpritesheetSource (UserLayer layer)
		=> layer is not GroupLayer
		&& layer.Name.StartsWith ("source-sheet", StringComparison.Ordinal)
		&& TryGetSpritesheetAttempt (layer, out _, out _);

	private static bool IsDirectionSheetSource (UserLayer layer)
		=> IsSpritesheetSource (layer) && layer.Parent?.Parent?.Name == "direction-sheet";

	private static bool TryGetSpritesheetAttempt (
		UserLayer source,
		out UserLayer? attempt,
		out AI.SpritesheetAttemptInfo? info)
	{
		attempt = source.Parent;
		info = null;
		if (attempt is not GroupLayer || !attempt.Metadata.TryGetValue (spritesheet_attempt_metadata, out string? json))
			return false;
		try {
			info = JsonSerializer.Deserialize<AI.SpritesheetAttemptInfo> (json);
			return info is not null;
		} catch (JsonException) {
			return false;
		}
	}

	private static string GetAiReferenceFileName (UserLayer layer, int index)
		=> IsCharacterAnchor (layer)
			? "character-anchor.png"
			: $"layer-{index}.png";

	private static bool IsCharacterAnchor (UserLayer layer)
		=> layer.Metadata.ContainsKey (spritesheet_anchor_metadata);

	private static void SelectDefaultCharacterAnchor (
		IReadOnlyList<(Gtk.CheckButton Button, UserLayer Layer)> layerChoices)
	{
		(UserLayer Layer, Gtk.CheckButton Button) latest = layerChoices
			.Where (choice => choice.Layer.Metadata.ContainsKey (spritesheet_anchor_metadata))
			.Select (choice => (choice.Layer, choice.Button))
			.LastOrDefault ();
		if (latest.Button is not null)
			latest.Button.Active = true;
	}

	private static AI.SpritesheetAttemptInfo CreateSpritesheetAttemptInfo (
		AI.SpritesheetPromptCatalog catalog,
		IReadOnlyList<(Gtk.CheckButton Button, AI.SpritesheetDirection Direction)> directionChoices,
		bool directionSheet,
		int actionIndex,
		double frameCount,
		int backgroundIndex,
		Size size,
		Gtk.TextBuffer promptBuffer)
	{
		string[] ids = directionChoices.Where (choice => choice.Button.Active).Select (choice => choice.Direction.Id).ToArray ();
		int frames = directionSheet ? 1 : (int) frameCount;
		(int columns, int rows) = AI.SpritesheetPromptCatalog.CalculateGrid (ids.Length * frames, size);
		promptBuffer.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
		return new (directionSheet, directionSheet ? "direction-sheet" : catalog.Actions[actionIndex].Id, ids, frames, columns, rows,
			catalog.Backgrounds[backgroundIndex].Id, size, promptBuffer.GetText (start, end, true).Trim (),
			AI.AiRequestSettings.GetImageService (PintaCore.Settings), AI.AiRequestSettings.GetGptProvider (PintaCore.Settings), 1);
	}

	private static bool IsSpritesheetFrame (UserLayer layer)
		=> layer is not GroupLayer
		&& layer.Name.StartsWith ("frame-", StringComparison.Ordinal)
		&& !layer.Name.Contains ("-cutout-", StringComparison.Ordinal)
		&& layer.Parent?.Parent is UserLayer attempt
		&& attempt.Metadata.ContainsKey (spritesheet_attempt_metadata);

	private static string GetCutoutResultName (UserLayer source)
	{
		if (!IsSpritesheetFrame (source) || source.Parent is null)
			return Translations.GetString ("Transparent Cutout");

		int next = 1;
		while (source.Parent.Children.Any (layer => layer.Name == $"{source.Name}-cutout-{next:D2}"))
			next++;
		return $"{source.Name}-cutout-{next:D2}";
	}

	private static byte[] CreateSurfacePng (Cairo.ImageSurface surface)
	{
		using GdkPixbuf.Pixbuf pixbuf = surface.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static UserLayer AddAiResultLayer (Document document, string name, Size size)
	{
		if (size == document.ImageSize)
			return document.Layers.AddNewLayer (name);

		UserLayer current = document.Layers.CurrentUserLayer;
		LayerPosition position = document.Layers.GetPosition (current);
		UserLayer layer = document.Layers.CreateLayer (name, size.Width, size.Height);
		document.Layers.Insert (layer, new LayerPosition (position.Parent, position.Index + 1));
		document.Layers.SetCurrentUserLayer (layer);
		return layer;
	}

	private async Task<SpritesheetSplitOptions?> PromptSpritesheetSplitAsync (
		UserLayer source,
		AI.SpritesheetAttemptInfo info)
	{
		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("Split Spritesheet");
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget submit = dialog.AddButton (Translations.GetString ("Split"), (int) Gtk.ResponseType.Ok);
		submit.AddCssClass (AdwaitaStyles.SuggestedAction);

		Gtk.SpinButton columns = Gtk.SpinButton.NewWithRange (1, 128, 1);
		Gtk.SpinButton rows = Gtk.SpinButton.NewWithRange (1, 128, 1);
		Gtk.SpinButton width = Gtk.SpinButton.NewWithRange (1, source.Surface.Width, 1);
		Gtk.SpinButton height = Gtk.SpinButton.NewWithRange (1, source.Surface.Height, 1);
		Gtk.SpinButton offsetX = Gtk.SpinButton.NewWithRange (0, source.Surface.Width - 1, 1);
		Gtk.SpinButton offsetY = Gtk.SpinButton.NewWithRange (0, source.Surface.Height - 1, 1);
		Gtk.SpinButton gapX = Gtk.SpinButton.NewWithRange (0, source.Surface.Width - 1, 1);
		Gtk.SpinButton gapY = Gtk.SpinButton.NewWithRange (0, source.Surface.Height - 1, 1);
		Gtk.CheckButton alignCharacter = Gtk.CheckButton.NewWithLabel (
			Translations.GetString ("Detect and align character registration"));
		alignCharacter.Active = true;
		columns.Value = info.Columns;
		rows.Value = info.Rows;
		width.Value = source.Surface.Width / info.Columns;
		height.Value = source.Surface.Height / info.Rows;

		Gtk.Grid grid = CreateSplitGrid ([
			(Translations.GetString ("Columns:"), columns),
			(Translations.GetString ("Rows:"), rows),
			(Translations.GetString ("Cell width:"), width),
			(Translations.GetString ("Cell height:"), height),
			(Translations.GetString ("Left offset:"), offsetX),
			(Translations.GetString ("Top offset:"), offsetY),
			(Translations.GetString ("Horizontal gap:"), gapX),
			(Translations.GetString ("Vertical gap:"), gapY),
		]);
		Gtk.Label preview = Gtk.Label.New (string.Empty);
		preview.Halign = Gtk.Align.Start;
		preview.Wrap = true;
		Gtk.Box content = dialog.GetContentAreaBox ();
		content.Spacing = 8;
		content.SetAllMargins (12);
		content.Append (grid);
		content.Append (alignCharacter);
		content.Append (preview);

		void Refresh ()
		{
			SpritesheetSplitOptions value = ReadSplitOptions (
				alignCharacter, columns, rows, width, height, offsetX, offsetY, gapX, gapY);
			bool valid = IsValidSplit (source, info, value);
			submit.Sensitive = valid;
			preview.SetText (valid
				? BuildSplitPreview (info, value)
				: Translations.GetString ("The grid exceeds the source image or does not contain all requested frames."));
		}
		foreach (Gtk.SpinButton spinner in new[] { columns, rows, width, height, offsetX, offsetY, gapX, gapY })
			spinner.OnValueChanged += (_, _) => Refresh ();
		Refresh ();

		return await dialog.RunAsync () == Gtk.ResponseType.Ok
			? ReadSplitOptions (alignCharacter, columns, rows, width, height, offsetX, offsetY, gapX, gapY)
			: null;
	}

	private static Gtk.Grid CreateSplitGrid (IReadOnlyList<(string Label, Gtk.SpinButton Input)> rows)
	{
		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = 6;
		grid.ColumnSpacing = 8;
		for (int index = 0; index < rows.Count; index++) {
			Gtk.Label label = Gtk.Label.New (rows[index].Label);
			label.Halign = Gtk.Align.End;
			grid.Attach (label, 0, index, 1, 1);
			grid.Attach (rows[index].Input, 1, index, 1, 1);
		}
		return grid;
	}

	private static SpritesheetSplitOptions ReadSplitOptions (Gtk.CheckButton alignCharacter, params Gtk.SpinButton[] values)
		=> new ((int) values[0].Value, (int) values[1].Value, (int) values[2].Value, (int) values[3].Value,
			(int) values[4].Value, (int) values[5].Value, (int) values[6].Value, (int) values[7].Value,
			alignCharacter.Active);

	private static bool IsValidSplit (UserLayer source, AI.SpritesheetAttemptInfo info, SpritesheetSplitOptions split)
	{
		int total = info.DirectionIds.Count * info.FrameCount;
		long right = split.OffsetX + (long) split.Columns * split.CellWidth + (long) (split.Columns - 1) * split.GapX;
		long bottom = split.OffsetY + (long) split.Rows * split.CellHeight + (long) (split.Rows - 1) * split.GapY;
		return split.Columns * split.Rows >= total && right <= source.Surface.Width && bottom <= source.Surface.Height;
	}

	private static string BuildSplitPreview (AI.SpritesheetAttemptInfo info, SpritesheetSplitOptions split)
	{
		List<string> mappings = [];
		int cell = 1;
		foreach (string direction in info.DirectionIds) {
			int last = cell + info.FrameCount - 1;
			mappings.Add (cell == last ? $"{cell}: {direction}" : $"{cell}-{last}: {direction}");
			cell = last + 1;
		}
		return $"{split.Columns} x {split.Rows}, row-major. {string.Join ("; ", mappings)}";
	}

	private static void ApplySpritesheetSplit (
		Document document,
		UserLayer attempt,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitOptions split)
	{
		CompoundHistoryItem history = new (Resources.Icons.ImageCrop, Translations.GetString ("Split Spritesheet"));
		bool newAttempt = attempt.Children.Any (child => child is GroupLayer);
		if (newAttempt)
			(attempt, source) = CreateResplitAttempt (document, attempt, source, history);
		UserLayer last = source;
		for (int directionIndex = 0; directionIndex < info.DirectionIds.Count; directionIndex++) {
			GroupLayer direction = document.Layers.CreateGroupLayer (info.DirectionIds[directionIndex]);
			for (int frameIndex = 0; frameIndex < info.FrameCount; frameIndex++) {
				int cell = directionIndex * info.FrameCount + frameIndex;
				UserLayer frame = CreateFrameLayer (document, source, split, cell, $"frame-{frameIndex + 1:D2}");
				if (split.AlignCharacter)
					AlignSpriteFrame (frame.Surface, info.BackgroundId, alignBaseline: info.ActionId != "jump");
				direction.InsertChild (direction.Children.Count, frame);
				last = frame;
			}
			document.Layers.Insert (direction, new LayerPosition (attempt, attempt.Children.Count));
			if (!newAttempt)
				history.Push (CreateAddHistory (document, direction));
		}
		string splitJson = JsonSerializer.Serialize (split);
		SpritesheetMetadataHistoryItem metadataHistory = new (attempt, spritesheet_split_metadata, splitJson);
		SetMetadata (attempt, spritesheet_split_metadata, splitJson);
		history.Push (metadataHistory);
		document.Layers.SetCurrentUserLayer (last);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
	}

	private static (UserLayer Attempt, UserLayer Source) CreateResplitAttempt (
		Document document,
		UserLayer currentAttempt,
		UserLayer currentSource,
		CompoundHistoryItem history)
	{
		UserLayer action = currentAttempt.Parent
			?? throw new InvalidOperationException ("A spritesheet attempt must belong to an action group.");
		GroupLayer attempt = document.Layers.CreateGroupLayer (GetNextAttemptName (action));
		foreach ((string key, string value) in currentAttempt.Metadata)
			attempt.Metadata.Add (key, value);

		UserLayer source = document.Layers.CreateLayer ("source-sheet", currentSource.Surface.Width, currentSource.Surface.Height);
		using (Cairo.Context context = new (source.Surface)) {
			context.SetSourceSurface (currentSource.Surface, 0, 0);
			context.Paint ();
		}
		foreach ((string key, string value) in currentSource.Metadata)
			source.Metadata.Add (key, value);
		attempt.InsertChild (0, source);
		document.Layers.Insert (attempt, new LayerPosition (action, action.Children.Count));
		history.Push (CreateAddHistory (document, attempt));
		return (attempt, source);
	}

	private static UserLayer CreateFrameLayer (
		Document document,
		UserLayer source,
		SpritesheetSplitOptions split,
		int cell,
		string name)
	{
		int column = cell % split.Columns;
		int row = cell / split.Columns;
		int x = split.OffsetX + column * (split.CellWidth + split.GapX);
		int y = split.OffsetY + row * (split.CellHeight + split.GapY);
		UserLayer frame = document.Layers.CreateLayer (name, split.CellWidth, split.CellHeight);
		using Cairo.Context context = new (frame.Surface);
		context.SetSourceSurface (source.Surface, -x, -y);
		context.Paint ();
		frame.Surface.MarkDirty ();
		return frame;
	}

	private sealed record SpritesheetSplitOptions (
		int Columns,
		int Rows,
		int CellWidth,
		int CellHeight,
		int OffsetX,
		int OffsetY,
		int GapX,
		int GapY,
		bool AlignCharacter);

	private static void SetMetadata (UserLayer layer, string key, string? value)
	{
		if (value is null)
			layer.Metadata.Remove (key);
		else
			layer.Metadata[key] = value;
	}

	private sealed class SpritesheetMetadataHistoryItem : BaseHistoryItem
	{
		private readonly UserLayer layer;
		private readonly string key;
		private readonly string? old_value;
		private readonly string? new_value;

		public SpritesheetMetadataHistoryItem (UserLayer layer, string key, string? newValue)
		{
			this.layer = layer;
			this.key = key;
			old_value = layer.Metadata.GetValueOrDefault (key);
			new_value = newValue;
		}

		public override void Undo () => SetMetadata (layer, key, old_value);
		public override void Redo () => SetMetadata (layer, key, new_value);
	}
}
