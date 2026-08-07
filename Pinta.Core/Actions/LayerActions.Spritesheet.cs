using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private const string spritesheet_attempt_metadata = "pinta.spritesheet.attempt";
	private const string spritesheet_anchor_metadata = "pinta.spritesheet.character-anchor";

	private static bool CanCreateSpritesheetAnimation (UserLayer layer)
		=> layer is not AnimationOutputLayer && layer is not GroupLayer && layer.IsEditable;

	private static bool CanEditSpritesheetAnimation (UserLayer layer)
		=> layer is SpriteSheetLayer;

	private static bool CanEditSingleDirectionAnimation (UserLayer layer)
		=> layer is SingleDirectionAnimationLayer;

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
		GroupLayer root = FindOrCreateGroup (document, null, "multi-direction-animation", history);
		MoveSpritesheetRootToTop (document, root, history);
		GroupLayer branch = FindOrCreateGroup (
			document,
			root,
			info.DirectionSheet ? "direction-set" : "actions",
			history);
		GroupLayer action = info.DirectionSheet
			? branch
			: FindOrCreateGroup (document, branch, info.ActionId, history);
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

	private static void MoveSpritesheetRootToTop (
		Document document,
		GroupLayer root,
		CompoundHistoryItem history)
	{
		LayerPosition oldPosition = document.Layers.GetPosition (root);
		if (oldPosition.Parent is not null || oldPosition.Index == document.Layers.RootLayers.Count - 1)
			return;

		LayerPosition newPosition = new (null, document.Layers.RootLayers.Count);
		document.Layers.MoveLayer (root, newPosition);
		history.Push (new MoveLayerHistoryItem (
			Resources.StandardIcons.LayerMoveUp,
			Translations.GetString ("Move Layer"),
			root,
			oldPosition,
			newPosition));
	}

	private GroupLayer FindOrCreateGroup (
		Document document,
		UserLayer? parent,
		string name,
		CompoundHistoryItem history)
	{
		IReadOnlyList<UserLayer> children = parent?.Children ?? document.Layers.RootLayers;
		if (children.FirstOrDefault (layer => layer is GroupLayer && layer is not AnimationOutputLayer && layer.Name == name) is GroupLayer existing)
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

	private static bool IsDirectionSheetSource (UserLayer layer)
		=> layer is not GroupLayer
		&& TryGetSpritesheetAttempt (layer, out _, out AI.SpritesheetAttemptInfo? info)
		&& info?.DirectionSheet == true;

	private static bool TryGetSpritesheetAttempt (
		UserLayer source,
		out UserLayer? attempt,
		out AI.SpritesheetAttemptInfo? info)
	{
		attempt = source.Parent;
		info = null;
		if (attempt is not GroupLayer)
			return false;
		if (!source.Metadata.TryGetValue (spritesheet_attempt_metadata, out string? json)
			&& !attempt.Metadata.TryGetValue (spritesheet_attempt_metadata, out json))
			return false;
		try {
			info = JsonSerializer.Deserialize<AI.SpritesheetAttemptInfo> (json);
			return info is not null;
		} catch (JsonException) {
			return false;
		}
	}

	private static bool TryGetSpritesheetAttemptInfo (UserLayer attempt, out AI.SpritesheetAttemptInfo? info)
	{
		info = null;
		if (!attempt.Metadata.TryGetValue (spritesheet_attempt_metadata, out string? json))
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
		bool directionSheet,
		int actionIndex,
		double frameCount,
		Size size,
		Gtk.TextBuffer promptBuffer)
	{
		string[] ids = [.. catalog.DirectionIds];
		int frames = directionSheet ? 1 : (int) frameCount;
		(int columns, int rows) = AI.SpritesheetPromptCatalog.CalculateGrid (ids.Length * frames, size);
		promptBuffer.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
		return new (directionSheet, directionSheet ? "direction-sheet" : catalog.Actions[actionIndex].Id, ids, frames, columns, rows,
			AI.SpritesheetPromptCatalog.FixedBackgroundId, size, promptBuffer.GetText (start, end, true).Trim (),
			AI.AiRequestSettings.GetImageService (PintaCore.Settings), AI.AiRequestSettings.GetImageProvider (PintaCore.Settings), 1);
	}

	private static AI.SpritesheetAttemptInfo CreateSingleDirectionAttemptInfo (
		Size size,
		Gtk.TextBuffer promptBuffer)
	{
		promptBuffer.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
		return new (false, "prompt", [SingleDirectionAnimationLayer.DefaultDirectionId], 1,
			1, 1, AI.SpritesheetPromptCatalog.FixedBackgroundId, size,
			promptBuffer.GetText (start, end, true).Trim (),
			AI.AiRequestSettings.GetImageService (PintaCore.Settings), AI.AiRequestSettings.GetImageProvider (PintaCore.Settings), 1);
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

		LayerPosition position = document.Layers.HasSelectedLayer
			? document.Layers.GetPosition (document.Layers.CurrentUserLayer)
			: new LayerPosition (null, document.Layers.RootLayers.Count);
		UserLayer layer = document.Layers.CreateLayer (name, size.Width, size.Height);
		if (document.Layers.HasSelectedLayer)
			position = position with { Index = position.Index + 1 };
		document.Layers.Insert (layer, position);
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
