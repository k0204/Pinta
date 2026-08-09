using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandlePintaCoreActionsLayersImageSplitActivated (object sender, EventArgs e)
	{
		if (auto_split_running || workspace.ActiveDocumentOrDefault is not Document document
			|| !document.Layers.HasSelectedLayer)
			return;

		UserLayer source = document.Layers.CurrentUserLayer;
		if (!source.IsEditable)
			return;

		tools.Commit ();
		auto_split_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		try {
			IReadOnlyList<AI.AiProviderInfo> providers = PintaCore.AiProviders.ChatProviders;
			using AutoSplitDialog dialog = new (
				chrome.MainWindow,
				source,
				providers,
				provider => sprite_segmentation.AnalyzeAsync (
					CreateSurfacePng (source.Surface),
					source.Surface.Width,
					source.Surface.Height,
					provider),
				EnsureAiLoggedIn);
			IReadOnlyList<RectangleI>? regions = await dialog.RunAsync ();
			if (regions is not null)
				ApplyAutoSplit (document, source, regions);
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Auto Split Failed"),
				Translations.GetString ("The image could not be split. Check the image and API settings, then try again."),
				ex.ToString ());
		} finally {
			auto_split_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
		}
	}

	private static void ApplyAutoSplit (
		Document document,
		UserLayer source,
		IReadOnlyList<RectangleI> regions)
	{
		CompoundHistoryItem history = new (
			Resources.Icons.ImageCrop,
			Translations.GetString ("Split Image"));
		ImageSurface original = source.Surface.Clone ();
		for (int index = 0; index < regions.Count; index++) {
			RectangleI bounds = regions[index];
			UserLayer child = CreateAutoSplitChild (document, source, original, bounds, index);
			LayerPosition position = new (source, source.Children.Count);
			document.Layers.Insert (child, position);
			history.Push (new AddLayerHistoryItem (
				Resources.Icons.ImageCrop,
				Translations.GetString ("Split Region {0}", index + 1),
				child,
				position));
		}

		source.Clear ();
		history.Push (new SimpleHistoryItem (string.Empty, string.Empty, original, source));
		document.Layers.SetCurrentUserLayer (source);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
	}

	private static UserLayer CreateAutoSplitChild (
		Document document,
		UserLayer source,
		ImageSurface original,
		RectangleI bounds,
		int index)
	{
		UserLayer child = document.Layers.CreateLayer (
			Translations.GetString ("Split {0}", index + 1),
			bounds.Width,
			bounds.Height);
		using (Context context = new (child.Surface)) {
			context.SetSourceSurface (original, -bounds.X, -bounds.Y);
			context.Paint ();
		}

		Matrix transform = source.Transform.Clone ();
		transform.Translate (bounds.X, bounds.Y);
		child.Transform = transform;
		child.Opacity = source.Opacity;
		child.BlendMode = source.BlendMode;
		child.Metadata["pinta.auto-split.x"] = bounds.X.ToString ();
		child.Metadata["pinta.auto-split.y"] = bounds.Y.ToString ();
		child.Metadata["pinta.auto-split.width"] = bounds.Width.ToString ();
		child.Metadata["pinta.auto-split.height"] = bounds.Height.ToString ();
		return child;
	}
}
