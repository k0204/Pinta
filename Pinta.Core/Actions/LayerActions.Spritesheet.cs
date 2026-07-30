using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
