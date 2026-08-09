using System;
using System.Collections.Generic;
using Pinta.Core;

namespace Pinta.Gui.Widgets;

public sealed partial class LayersListView
{
	private void HandleSelectionChanged (
		Gtk.SelectionModel sender,
		EventArgs e)
	{
		ArgumentNullException.ThrowIfNull (active_document);
		if (changing_selection)
			return;

		List<UserLayer> modelSelected = GetModelSelectedLayers ();
		try {
			changing_selection = true;
			active_document.Layers.SetSelectedUserLayers (modelSelected);
		} finally {
			changing_selection = false;
		}

		SyncSelectionModel (active_document.Layers.SelectedUserLayers);
		PintaCore.Actions.Layers.MergeSelectedLayers.Sensitive =
			PintaCore.Actions.Layers.CanMergeLayers (GetSelectedLayers ());
	}

	private void HandleLayerSelectionRequested (object? sender, LayerSelectionEventArgs e)
	{
		ArgumentNullException.ThrowIfNull (active_document);
		if (e.Layer.Locked)
			return;

		foreach (LayersListViewItemWidget widget in row_widgets)
			if (widget.UserLayer != e.Layer)
				widget.CommitRename ();

		if (!TryFindModelIndex (e.Layer, out int index))
			return;
		bool shift = e.Modifiers.IsShiftPressed ();
		bool control = e.Modifiers.IsControlPressed ();

		try {
			changing_selection = true;
			if (shift && selection_anchor is not null && TryFindModelIndex (selection_anchor, out int anchorIndex)) {
				int first = Math.Min (anchorIndex, index);
				int last = Math.Max (anchorIndex, index);
				selection_model.SelectItem ((uint) first, unselectRest: true);
				for (int i = first + 1; i <= last; i++)
					selection_model.SelectItem ((uint) i, unselectRest: false);
			} else if (control && selection_model.IsSelected ((uint) index)) {
				selection_model.UnselectItem ((uint) index);
			} else {
				selection_model.SelectItem ((uint) index, unselectRest: !control);
			}
		} finally {
			changing_selection = false;
		}

		active_document.Layers.SetSelectedUserLayers (GetModelSelectedLayers ());
		SyncSelectionModel (active_document.Layers.SelectedUserLayers);

		List<UserLayer> selected = GetSelectedLayers ();
		if (selected.Count == 0) {
			active_document.Layers.SetSelectedUserLayers ([e.Layer]);
			selected = GetSelectedLayers ();
		}

		active_document.ResetSelectionPaths ();
		active_document.Workspace.Invalidate ();
		if (!shift)
			selection_anchor = e.Layer;

		PintaCore.Actions.Layers.MergeSelectedLayers.Sensitive =
			PintaCore.Actions.Layers.CanMergeLayers (selected);
	}

	private void HandleSelectedLayerChanged (object? sender, EventArgs e)
	{
		ArgumentNullException.ThrowIfNull (active_document);
		if (changing_selection)
			return;

		SyncSelectionModel (active_document.Layers.SelectedUserLayers);
		if (!active_document.Layers.HasSelectedLayer) {
			selection_anchor = null;
			return;
		}

		if (TryFindModelIndex (active_document.Layers.CurrentUserLayer, out int index))
			list_view.ScrollTo ((uint) index, Gtk.ListScrollFlags.None, null);
	}

	private List<UserLayer> GetSelectedLayers ()
		=> active_document is null ? [] : [.. active_document.Layers.SelectedUserLayers];

	private List<UserLayer> GetModelSelectedLayers ()
	{
		List<UserLayer> layers = [];
		for (uint i = 0; i < list_model.GetNItems (); i++) {
			if (!selection_model.IsSelected (i))
				continue;

			LayersListViewItem entry = (LayersListViewItem) list_model.GetObject (i)!;
			if (entry.UserLayer is not null)
				layers.Add (entry.UserLayer);
		}

		return layers;
	}

	private void SyncSelectionModel (IEnumerable<UserLayer> layers)
	{
		HashSet<UserLayer> selected = [.. layers];
		try {
			changing_selection = true;
			selection_model.UnselectAll ();
			for (uint i = 0; i < list_model.GetNItems (); i++) {
				LayersListViewItem entry = (LayersListViewItem) list_model.GetObject (i)!;
				if (entry.UserLayer is not null && selected.Contains (entry.UserLayer))
					selection_model.SelectItem (i, unselectRest: false);
			}
		} finally {
			changing_selection = false;
		}
	}
}
