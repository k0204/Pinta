using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	public void DeleteLayers (IReadOnlyCollection<UserLayer> layers)
	{
		Document doc = workspace.ActiveDocument;
		List<UserLayer> allLayers = [.. doc.Layers.AllLayers];
		HashSet<UserLayer> selected = [.. layers.Where (allLayers.Contains)];
		List<UserLayer> targets = [..
			selected
				.Where (layer => !HasSelectedAncestor (layer, selected))
				.OrderByDescending (allLayers.IndexOf)];

		if (targets.Count == 0)
			return;

		tools.Commit ();

		CompoundHistoryItem history = new (
			Resources.Icons.LayerDelete,
			Translations.GetString ("Delete Layer"));

		if (allLayers.All (layer => IsInSelectedTree (layer, selected)))
			AddReplacementLayer (doc, history);

		foreach (UserLayer layer in targets) {
			history.Push (new DeleteLayerHistoryItem (
				string.Empty,
				string.Empty,
				layer,
				doc.Layers.GetPosition (layer)));
			doc.Layers.DeleteLayer (layer);
		}

		doc.History.PushNewItem (history);
	}

	private static bool HasSelectedAncestor (UserLayer layer, HashSet<UserLayer> selected)
	{
		for (UserLayer? parent = layer.Parent; parent is not null; parent = parent.Parent)
			if (selected.Contains (parent))
				return true;

		return false;
	}

	private static bool IsInSelectedTree (UserLayer layer, HashSet<UserLayer> selected)
		=> selected.Contains (layer) || HasSelectedAncestor (layer, selected);

	private static void AddReplacementLayer (Document doc, CompoundHistoryItem history)
	{
		UserLayer replacement = doc.Layers.CreateLayer ();
		LayerPosition position = new (null, doc.Layers.RootLayers.Count);
		doc.Layers.Insert (replacement, position);
		history.Push (new AddLayerHistoryItem (string.Empty, string.Empty, replacement, position));
	}
}
