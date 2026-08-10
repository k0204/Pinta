using System;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private enum LayerAlignment
	{
		Left,
		CenterHorizontal,
		Right,
		Top,
		CenterVertical,
		Bottom,
	}

	private static bool CanAlignSelectedLayers (Document document)
		=> document.Layers.TryGetSelectedLayerTreeBounds (out _);

	private void HandleAlignLayersLeftActivated (object sender, EventArgs e)
		=> AlignSelectedLayers (LayerAlignment.Left, Translations.GetString ("Align Layers Left"));

	private void HandleAlignLayersCenterHorizontalActivated (object sender, EventArgs e)
		=> AlignSelectedLayers (LayerAlignment.CenterHorizontal, Translations.GetString ("Align Layers Center Horizontally"));

	private void HandleAlignLayersRightActivated (object sender, EventArgs e)
		=> AlignSelectedLayers (LayerAlignment.Right, Translations.GetString ("Align Layers Right"));

	private void HandleAlignLayersTopActivated (object sender, EventArgs e)
		=> AlignSelectedLayers (LayerAlignment.Top, Translations.GetString ("Align Layers Top"));

	private void HandleAlignLayersCenterVerticalActivated (object sender, EventArgs e)
		=> AlignSelectedLayers (LayerAlignment.CenterVertical, Translations.GetString ("Align Layers Center Vertically"));

	private void HandleAlignLayersBottomActivated (object sender, EventArgs e)
		=> AlignSelectedLayers (LayerAlignment.Bottom, Translations.GetString ("Align Layers Bottom"));

	private void AlignSelectedLayers (LayerAlignment alignment, string historyText)
	{
		if (workspace.ActiveDocumentOrDefault is not Document document
			|| !document.Layers.TryGetSelectedLayerTreeBounds (out RectangleD bounds))
			return;

		PointD delta = CalculateAlignmentDelta (alignment, bounds, document.ImageSize);
		if (delta == PointD.Zero)
			return;

		tools.Commit ();
		CompoundHistoryItem history = new (Resources.Icons.EffectsAlignObject, historyText);
		foreach (UserLayer root in document.Layers.GetSelectedLayerRoots ()) {
			document.Layers.TranslateLayerTree (root, delta);
			history.Push (new MoveLayerTreeHistoryItem (
				Resources.Icons.EffectsAlignObject,
				string.Empty,
				document,
				root,
				delta));
		}

		document.History.PushNewItem (history);
	}

	private static PointD CalculateAlignmentDelta (
		LayerAlignment alignment,
		RectangleD bounds,
		Size canvasSize)
	{
		double targetX = alignment switch {
			LayerAlignment.Left or LayerAlignment.CenterHorizontal or LayerAlignment.Right
				=> alignment switch {
					LayerAlignment.Left => 0,
					LayerAlignment.CenterHorizontal => (canvasSize.Width - bounds.Width) / 2,
					_ => canvasSize.Width - bounds.Width,
				},
			_ => bounds.X,
		};
		double targetY = alignment switch {
			LayerAlignment.Top or LayerAlignment.CenterVertical or LayerAlignment.Bottom
				=> alignment switch {
					LayerAlignment.Top => 0,
					LayerAlignment.CenterVertical => (canvasSize.Height - bounds.Height) / 2,
					_ => canvasSize.Height - bounds.Height,
				},
			_ => bounds.Y,
		};

		return new PointD (targetX - bounds.X, targetY - bounds.Y);
	}
}
