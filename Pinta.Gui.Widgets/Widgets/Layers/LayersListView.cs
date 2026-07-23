//
// LayersListWidget.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//       Greg Lowe <greg@vis.net.nz>
//
// Copyright (c) 2010 Jonathan Pobst
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Pinta.Core;

namespace Pinta.Gui.Widgets;

[GObject.Subclass<Gtk.ScrolledWindow>]
public sealed partial class LayersListView
{
	private Gio.ListStore list_model;
	private Gtk.SingleSelection selection_model;
	private Gtk.ListView list_view;
	private Document? active_document;
	private bool changing_selection = false;
	private LayersListViewItemWidget? drop_hint_widget;
	private readonly List<LayersListViewItemWidget> row_widgets = [];

	public static new LayersListView New ()
		=> NewWithProperties ([]);

	[MemberNotNull (nameof (list_model))]
	[MemberNotNull (nameof (selection_model))]
	[MemberNotNull (nameof (list_view))]
	partial void Initialize ()
	{
		// --- Control creaton

		Gio.ListStore listModel = Gio.ListStore.New (LayersListViewItem.GetGType ());

		Gtk.SingleSelection selectionModel = Gtk.SingleSelection.New (listModel);
		selectionModel.OnSelectionChanged += HandleSelectionChanged;

		Gtk.SignalListItemFactory factory = Gtk.SignalListItemFactory.New ();
		factory.OnSetup += HandleFactorySetup;
		factory.OnBind += HandleFactoryBind;

		Gtk.ListView listView = Gtk.ListView.New (selectionModel, factory);
		listView.CanFocus = false;
		listView.OnActivate += HandleRowActivated;

		// --- Initialization (Gtk.Widget)

		CanFocus = false;
		SetSizeRequest (200, 200);

		// --- Initialization (Gtk.ScrolledWindow)

		SetPolicy (Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
		SetChild (listView);

		// --- References to keep

		list_model = listModel;
		selection_model = selectionModel;
		list_view = listView;

		// --- Other initialization (TODO: remove references to PintaCore)

		PintaCore.Workspace.ActiveDocumentChanged += HandleActiveDocumentChanged;
	}

	private void HandleFactorySetup (
		Gtk.SignalListItemFactory factory,
		Gtk.SignalListItemFactory.SetupSignalArgs args)
	{
		var item = (Gtk.ListItem) args.Object;
		LayersListViewItemWidget widget = LayersListViewItemWidget.New ();
		widget.LayerDragEnded += HandleLayerDragEnded;
		widget.LayerDragUpdated += HandleLayerDragUpdated;
		widget.LayerDragCanceled += HandleLayerDragCanceled;
		row_widgets.Add (widget);
		item.SetChild (widget);
	}

	private static void HandleFactoryBind (
		Gtk.SignalListItemFactory factory,
		Gtk.SignalListItemFactory.BindSignalArgs args)
	{
		var list_item = (Gtk.ListItem) args.Object;
		var model_item = (LayersListViewItem) list_item.GetItem ()!;
		var widget = (LayersListViewItemWidget) list_item.GetChild ()!;
		widget.SetItem (model_item);
	}

	private void HandleSelectionChanged (
		Gtk.SelectionModel sender,
		EventArgs e)
	{
		ArgumentNullException.ThrowIfNull (active_document);

		// If changing the current layer causes a history item to be added, ensure we
		// don't end up in an infinite loop when HandleHistoryChanged updates the
		// selection (see bug #1463)
		if (changing_selection)
			return;

		try {
			changing_selection = true;

			int model_idx = (int) selection_model.Selected;
			LayersListViewItem entry = (LayersListViewItem) list_model.GetObject ((uint) model_idx)!;

			if (entry.UserLayer is not null && active_document.Layers.CurrentUserLayer != entry.UserLayer)
				active_document.Layers.SetCurrentUserLayer (entry.UserLayer);
		} finally {
			changing_selection = false;
		}
	}

	private void HandleRowActivated (
		Gtk.ListView sender,
		Gtk.ListView.ActivateSignalArgs args)
	{
		// Open the layer properties dialog
		PintaCore.Actions.Layers.Properties.Activate ();
	}

	private void HandleActiveDocumentChanged (object? sender, EventArgs e)
	{
		Document? doc =
			PintaCore.Workspace.HasOpenDocuments
			? PintaCore.Workspace.ActiveDocument
			: null;

		if (active_document == doc)
			return;

		if (active_document != null) {
			active_document.History.HistoryItemAdded -= HandleHistoryChanged;
			active_document.History.ActionUndone -= HandleHistoryChanged;
			active_document.History.ActionRedone -= HandleHistoryChanged;
			active_document.Layers.LayerTreeChanged -= HandleLayerTreeChanged;
			active_document.Layers.SelectedLayerChanged -= HandleSelectedLayerChanged;
			active_document.Layers.LayerPropertyChanged -= HandleLayerPropertyChanged;
		}

		// Clear out old items and rebuild.
		list_model.RemoveMultiple (0, list_model.GetNItems ());

		active_document = doc;
		if (doc is null)
			return;

		foreach (var entry in EnumerateVisibleLayers (doc.Layers.RootLayers))
			list_model.Append (LayersListViewItem.New (doc, entry.Layer, entry.Depth));

		// Update our selection to match the document's active layer.
		int currentModelIndex = FindModelIndex (doc.Layers.CurrentUserLayer);
		selection_model.SelectItem ((uint) currentModelIndex, unselectRest: true);
		list_view.ScrollToSelectedItem (selection_model);

		doc.History.HistoryItemAdded += HandleHistoryChanged;
		doc.History.ActionUndone += HandleHistoryChanged;
		doc.History.ActionRedone += HandleHistoryChanged;
		doc.Layers.LayerTreeChanged += HandleLayerTreeChanged;
		doc.Layers.SelectedLayerChanged += HandleSelectedLayerChanged;
		doc.Layers.LayerPropertyChanged += HandleLayerPropertyChanged;
	}

	private void HandleHistoryChanged (object? sender, EventArgs e)
	{
		ArgumentNullException.ThrowIfNull (active_document);

		// Update the list view items to refresh their corresponding widgets.
		// This update should ideally be done through gobject property bindings instead, but we don't have the ability to add custom properties yet
		for (uint i = 0; i < list_model.GetNItems (); ++i) {
			LayersListViewItem item = (LayersListViewItem) list_model.GetObject (i)!;
			item.NotifyLayerModified ();
		}
	}

	private void HandleLayerTreeChanged (object? sender, EventArgs e)
	{
		ArgumentNullException.ThrowIfNull (active_document);

		RebuildModel ();
		HandleSelectedLayerChanged (sender, e);
	}

	private void HandleSelectedLayerChanged (object? sender, EventArgs e)
	{
		ArgumentNullException.ThrowIfNull (active_document);

		int index = FindModelIndex (active_document.Layers.CurrentUserLayer);
		selection_model.SelectItem ((uint) index, unselectRest: true);
		list_view.ScrollToSelectedItem (selection_model);
	}

	private void HandleLayerPropertyChanged (object? sender, EventArgs e)
	{
		// Treat the same as an undo event, and update the widgets.
		HandleHistoryChanged (sender, e);
	}

	private void HandleLayerDragUpdated (object? sender, LayerDragEventArgs args)
	{
		if (sender is not LayersListViewItemWidget source)
			return;

		if (!TryGetDropTarget (source, args.EndPoint, out LayerDropTarget target)) {
			ClearDropHint ();
			return;
		}

		SetDropHint (target.Widget, target.Hint);
	}

	private void HandleLayerDragCanceled (object? sender, EventArgs args)
	{
		ClearDropHint ();
	}

	private void HandleLayerDragEnded (object? sender, LayerDragEventArgs args)
	{
		ClearDropHint ();

		if (active_document is null || sender is not LayersListViewItemWidget source || source.UserLayer is null)
			return;

		UserLayer layer = source.UserLayer;

		if (!TryGetDropTarget (source, args.EndPoint, out LayerDropTarget target))
			return;

		LayerPosition oldPosition = active_document.Layers.GetPosition (layer);
		try {
			active_document.Layers.MoveLayer (layer, target.Position);
		} catch (ArgumentException) {
			return;
		} catch (InvalidOperationException) {
			return;
		}

		if (target.Position.Parent is not null) {
			target.Position.Parent.Expanded = true;
			active_document.Layers.NotifyLayerTreeChanged ();
		}

		LayerPosition newPosition = active_document.Layers.GetPosition (layer);
		if (oldPosition == newPosition)
			return;

		active_document.History.PushNewItem (new MoveLayerHistoryItem (
			Resources.StandardIcons.LayerMoveUp,
			Translations.GetString ("Move Layer"),
			layer,
			oldPosition,
			newPosition));
	}

	private bool TryGetDropTarget (
		LayersListViewItemWidget source,
		PointD sourcePoint,
		out LayerDropTarget target)
	{
		target = default;

		if (active_document is null || source.UserLayer is null)
			return false;

		if (!source.TranslateCoordinates (this, sourcePoint, out PointD dropPoint))
			return false;

		List<RowBounds> rows = GetVisibleRows ();
		if (rows.Count == 0)
			return false;

		UserLayer layer = source.UserLayer;

		if (dropPoint.Y < rows[0].Top) {
			target = new LayerDropTarget (rows[0].Widget, new LayerPosition (null, 0), LayerDropHint.Before);
			return true;
		}

		RowBounds last = rows[^1];
		if (dropPoint.Y >= last.Bottom) {
			target = new LayerDropTarget (last.Widget, new LayerPosition (null, active_document.Layers.RootLayers.Count), LayerDropHint.After);
			return true;
		}

		foreach (RowBounds row in rows) {
			if (dropPoint.Y < row.Top || dropPoint.Y >= row.Bottom)
				continue;

			if (row.Widget.UserLayer is not UserLayer targetLayer || layer == targetLayer)
				return false;

			double relativeY = dropPoint.Y - row.Top;
			double height = row.Bottom - row.Top;

			if (relativeY < height / 4d) {
				target = new LayerDropTarget (
					row.Widget,
					GetSiblingDropPosition (row.Widget, targetLayer, after: false, dropPoint.X),
					LayerDropHint.Before);
				return true;
			}

			if (relativeY > height * 3d / 4d) {
				target = new LayerDropTarget (
					row.Widget,
					GetSiblingDropPosition (row.Widget, targetLayer, after: true, dropPoint.X),
					LayerDropHint.After);
				return true;
			}

			target = new LayerDropTarget (
				row.Widget,
				new LayerPosition (targetLayer, targetLayer.Children.Count),
				LayerDropHint.Into);
			return true;
		}

		return false;
	}

	private LayerPosition GetSiblingDropPosition (
		LayersListViewItemWidget widget,
		UserLayer targetLayer,
		bool after,
		double dropX)
	{
		ArgumentNullException.ThrowIfNull (active_document);

		int desiredDepth = Math.Clamp ((int) (dropX / 24d), 0, widget.Depth);
		UserLayer insertionTarget = targetLayer;
		for (int depth = widget.Depth; depth > desiredDepth && insertionTarget.Parent is not null; --depth)
			insertionTarget = insertionTarget.Parent;

		LayerPosition position = active_document.Layers.GetPosition (insertionTarget);
		return after ? position with { Index = position.Index + 1 } : position;
	}

	private List<RowBounds> GetVisibleRows ()
	{
		List<RowBounds> rows = [];
		foreach (LayersListViewItemWidget widget in row_widgets) {
			if (widget.UserLayer is null)
				continue;

			if (!widget.TranslateCoordinates (this, PointD.Zero, out PointD rowPoint))
				continue;

			int height = widget.GetHeight ();
			if (height <= 0)
				continue;

			rows.Add (new RowBounds (widget, rowPoint.Y, rowPoint.Y + height));
		}

		rows.Sort ((a, b) => a.Top.CompareTo (b.Top));
		return rows;
	}

	private void SetDropHint (
		LayersListViewItemWidget widget,
		LayerDropHint hint)
	{
		if (drop_hint_widget != widget)
			ClearDropHint ();

		drop_hint_widget = widget;
		widget.SetDropHint (hint);
	}

	private void ClearDropHint ()
	{
		drop_hint_widget?.SetDropHint (LayerDropHint.None);
		drop_hint_widget = null;
	}

	private void RebuildModel ()
	{
		ArgumentNullException.ThrowIfNull (active_document);

		list_model.RemoveMultiple (0, list_model.GetNItems ());
		foreach (var entry in EnumerateVisibleLayers (active_document.Layers.RootLayers))
			list_model.Append (LayersListViewItem.New (active_document, entry.Layer, entry.Depth));
	}

	private int FindModelIndex (UserLayer layer)
	{
		for (uint i = 0; i < list_model.GetNItems (); ++i) {
			LayersListViewItem entry = (LayersListViewItem) list_model.GetObject (i)!;
			if (entry.UserLayer == layer)
				return (int) i;
		}

		return 0;
	}

	private static IEnumerable<(UserLayer Layer, int Depth)> EnumerateVisibleLayers (IEnumerable<UserLayer> layers, int depth = 0)
	{
		foreach (UserLayer layer in layers) {
			yield return (layer, depth);

			if (!layer.HasChildren || !layer.Expanded)
				continue;

			foreach (var child in EnumerateVisibleLayers (layer.Children, depth + 1))
				yield return child;
		}
	}

	private readonly record struct LayerDropTarget (
		LayersListViewItemWidget Widget,
		LayerPosition Position,
		LayerDropHint Hint);

	private readonly record struct RowBounds (
		LayersListViewItemWidget Widget,
		double Top,
		double Bottom);
}
