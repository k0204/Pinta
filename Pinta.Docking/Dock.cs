//
// Author:
//       Cameron White <cameronwhite91@gmail.com>
//
// Copyright (c) 2020 Cameron White
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
using System.IO;
using System.Linq;
using System.Text.Json;
using Pinta.Core;

namespace Pinta.Docking;

public sealed class DockItemVisibilityChangedEventArgs (string itemName, bool visible) : EventArgs
{
	public string ItemName { get; } = itemName;
	public bool Visible { get; } = visible;
}

/// <summary>
/// Hosts the one authoritative tree of dock groups and split regions.
/// </summary>
[GObject.Subclass<Gtk.Box>]
public sealed partial class Dock
{
	private const int LayoutVersion = 4;
	private const int DefaultFloatingWidth = 360;
	private const int DefaultFloatingHeight = 480;

	private readonly Dictionary<string, DockItem> items = [];
	private readonly HashSet<string> hidden_items = [];
	private readonly List<FloatingHost> floating_hosts = [];
	private readonly List<DockGroupView> rendered_groups = [];

	private Gtk.Application application = null!;
	private Gtk.Window owner = null!;
	private DockNode? main_root;
	private Gtk.Widget? main_widget;
	private bool tool_windows_visible = true;
	private bool drop_completed;
	private bool drag_cancelled;
	private DockItem? dragged_item;
	private bool dragged_from_floating;

	partial void Initialize ()
	{
		SetOrientation (Gtk.Orientation.Horizontal);
		Hexpand = true;
		Vexpand = true;
	}

	public static Dock New (Gtk.Application application, Gtk.Window owner)
	{
		Dock dock = NewWithProperties ([]);
		dock.application = application;
		dock.owner = owner;
		return dock;
	}

	public event EventHandler<DockItemVisibilityChangedEventArgs>? ItemVisibilityChanged;

	public bool ToolWindowsVisible {
		get => tool_windows_visible;
		set {
			if (tool_windows_visible == value)
				return;
			tool_windows_visible = value;
			Rebuild ();
		}
	}

	public void AddItem (DockItem item, DockPlacement placement)
	{
		if (!items.TryAdd (item.UniqueName, item))
			throw new ArgumentException ($"A dock item named '{item.UniqueName}' is already registered.", nameof (item));

		item.DefaultPlacement = placement;
		item.LabelChanged += (_, _) => Rebuild ();

		if (main_root is null && placement == DockPlacement.Center)
			main_root = new DockGroupNode ([item]);
		else
			InsertAtDefaultPlacement (item);

		Rebuild ();
	}

	public bool IsItemVisible (string itemName)
		=> items.ContainsKey (itemName) && !hidden_items.Contains (itemName);

	public void SetItemVisible (string itemName, bool visible)
	{
		if (!items.TryGetValue (itemName, out DockItem? item) || item.Locked)
			return;

		bool changed = visible ? hidden_items.Remove (itemName) : hidden_items.Add (itemName);
		if (!changed)
			return;

		ItemVisibilityChanged?.Invoke (this, new (itemName, visible));
		Rebuild ();
	}

	public void FloatItem (string itemName)
	{
		if (!items.TryGetValue (itemName, out DockItem? item) || item.Locked || IsInFloatingWindow (item))
			return;

		RemoveItemFromLayout (item);
		FloatingHost host = CreateFloatingHost (new DockGroupNode ([item]), DefaultFloatingWidth, DefaultFloatingHeight);
		floating_hosts.Add (host);
		hidden_items.Remove (itemName);
		Rebuild ();
	}

	public void ResetLayout ()
	{
		hidden_items.Clear ();
		foreach (FloatingHost host in floating_hosts)
			host.Window.Destroy ();
		floating_hosts.Clear ();
		main_root = CreateDefaultLayout ();
		NotifyAllVisibility ();
		Rebuild ();
	}

	public void SaveSettings (ISettingsService settings)
	{
		CaptureExtents (main_root);
		foreach (FloatingHost host in floating_hosts)
			CaptureExtents (host.Root);

		LayoutState state = new () {
			Version = LayoutVersion,
			Main = SaveNode (main_root),
			Hidden = [.. hidden_items],
			Floating = [.. floating_hosts.Select (host => new FloatingState {
				Root = SaveNode (host.Root),
				Width = Math.Max (160, host.Window.GetWidth ()),
				Height = Math.Max (120, host.Window.GetHeight ()),
			})],
		};

		settings.PutSetting (SettingNames.Layout, JsonSerializer.Serialize (state));
	}

	public void LoadSettings (ISettingsService settings)
	{
		string json = settings.GetSetting (SettingNames.Layout, string.Empty);
		if (string.IsNullOrWhiteSpace (json)) {
			ResetLayout ();
			return;
		}

		try {
			LayoutState state = JsonSerializer.Deserialize<LayoutState> (json)
				?? throw new InvalidDataException ("Dock layout is empty.");
			if (state.Version != LayoutVersion)
				throw new InvalidDataException ("Unsupported dock layout version.");

			HashSet<string> used = [];
			DockNode? restoredMain = RestoreNode (state.Main, used, allowLocked: true);
			if (restoredMain is null || items.Values.Where (item => item.Locked).Any (item => !ContainsItem (restoredMain, item)))
				throw new InvalidDataException ("Dock layout does not contain the canvas.");

			List<(DockNode Root, int Width, int Height)> restoredFloating = [];
			foreach (FloatingState floating in state.Floating ?? []) {
				DockNode? root = RestoreNode (floating.Root, used, allowLocked: false);
				if (root is not null)
					restoredFloating.Add ((root, Math.Clamp (floating.Width, 160, 4096), Math.Clamp (floating.Height, 120, 4096)));
			}

			main_root = restoredMain;
			foreach (DockItem item in items.Values.Where (item => !used.Contains (item.UniqueName)))
				InsertAtDefaultPlacement (item);

			hidden_items.Clear ();
			foreach (string itemName in state.Hidden ?? [])
				if (items.TryGetValue (itemName, out DockItem? item) && !item.Locked)
					hidden_items.Add (itemName);

			foreach (FloatingHost host in floating_hosts)
				host.Window.Destroy ();
			floating_hosts.Clear ();
			foreach (var floating in restoredFloating)
				floating_hosts.Add (CreateFloatingHost (floating.Root, floating.Width, floating.Height));

			NotifyAllVisibility ();
			Rebuild ();
		} catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException) {
			ResetLayout ();
		}
	}

	private void ConfigureDragSource (Gtk.Widget widget, DockItem item)
	{
		if (item.Locked)
			return;

		Gtk.DragSource source = Gtk.DragSource.New ();
		source.Actions = Gdk.DragAction.Move;
		source.OnPrepare += (_, _) => {
			using GObject.Value value = new (item.UniqueName);
			return Gdk.ContentProvider.NewForValue (value);
		};
		source.OnDragBegin += (_, _) => BeginDrag (item);
		source.OnDragCancel += (_, args) => {
			drag_cancelled = args.Reason != Gdk.DragCancelReason.NoTarget;
			return false;
		};
		source.OnDragEnd += (_, _) => EndDrag (item);
		widget.AddController (source);
	}

	private bool HandleDrop (DockGroupNode target, string itemName, double x, double y, int width, int height)
	{
		if (!items.TryGetValue (itemName, out DockItem? item) || item.Locked)
			return false;

		DockPlacement placement = GetDropPlacement (x, y, width, height);
		if (placement == DockPlacement.Center && target.Items.Any (candidate => candidate.Locked)) {
			drop_completed = true;
			return true;
		}

		drop_completed = true;
		// Let GTK finish dispatching the drop before rebuilding the widget tree.
		// Rebuilding here would detach the drag source and drop target while their
		// signal handlers are still running.
		GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT_IDLE, () => {
			MoveItem (item, target, placement, x, width, height);
			return false;
		});
		return true;
	}

	private void DockToMainEdge (DockItem item, DockPlacement placement)
	{
		if (item.Locked || placement == DockPlacement.Center || main_root is null)
			return;

		int addedExtent = GetDockExtent (item, placement, GetWidth (), GetHeight ());
		RemoveItemFromLayout (item);
		DockGroupNode group = new ([item]);
		main_root = WrapNode (main_root, group, placement, addedExtent);
		hidden_items.Remove (item.UniqueName);
		RemoveEmptyFloatingHosts ();
		Rebuild ();
	}

	private void BeginDrag (DockItem item)
	{
		dragged_item = item;
		dragged_from_floating = IsInFloatingWindow (item);
		drop_completed = false;
		drag_cancelled = false;
	}

	private void EndDrag (DockItem item)
	{
		foreach (DockGroupView group in rendered_groups)
			group.HideDropPreview ();

		if (dragged_item == item && !drop_completed && !drag_cancelled && !dragged_from_floating)
			FloatItem (item.UniqueName);

		dragged_item = null;
		drop_completed = false;
		drag_cancelled = false;
	}

	private void MoveItem (
		DockItem item,
		DockGroupNode target,
		DockPlacement placement,
		double x,
		int width,
		int height)
	{
		DockGroupNode? origin = FindGroup (item);
		if (origin is null)
			return;

		if (origin == target && origin.Items.Count == 1)
			return;

		int addedExtent = GetDockExtent (item, placement, width, height);
		origin.Items.Remove (item);
		if (origin.ActiveItem == item.UniqueName)
			origin.ActiveItem = origin.Items.FirstOrDefault ()?.UniqueName;
		PruneLayouts ();

		if (placement == DockPlacement.Center) {
			int index = width <= 0
				? target.Items.Count
				: Math.Clamp ((int) Math.Round (x / width * target.Items.Count), 0, target.Items.Count);
			target.Items.Insert (index, item);
			target.ActiveItem = item.UniqueName;
		} else {
			DockGroupNode group = new ([item]);
			ReplaceNodeInLayouts (target, WrapNode (target, group, placement, addedExtent));
		}

		hidden_items.Remove (item.UniqueName);
		RemoveEmptyFloatingHosts ();
		Rebuild ();
	}

	private void InsertAtDefaultPlacement (DockItem item)
	{
		if (main_root is null) {
			main_root = new DockGroupNode ([item]);
			return;
		}

		DockGroupNode? group = EnumerateGroups (main_root)
			.FirstOrDefault (candidate => candidate.Items.Any (existing => existing.DefaultPlacement == item.DefaultPlacement));
		if (group is not null && item.DefaultPlacement != DockPlacement.Center) {
			group.Items.Add (item);
			return;
		}

		if (item.DefaultPlacement == DockPlacement.Center) {
			DockGroupNode? center = EnumerateGroups (main_root).FirstOrDefault (candidate => candidate.Items.Any (existing => existing.Locked));
			(center ?? throw new InvalidOperationException ("The dock has no center group.")).Items.Add (item);
			return;
		}

		int extent = GetDefaultDockExtent ([item], item.DefaultPlacement);
		main_root = WrapNode (main_root, new DockGroupNode ([item]), item.DefaultPlacement, extent);
	}

	private DockNode? CreateDefaultLayout ()
	{
		DockItem? centerItem = items.Values.FirstOrDefault (item => item.DefaultPlacement == DockPlacement.Center);
		if (centerItem is null)
			return null;

		DockNode root = new DockGroupNode ([centerItem]);
		foreach (DockPlacement placement in new[] { DockPlacement.Left, DockPlacement.Right, DockPlacement.Top, DockPlacement.Bottom }) {
			List<DockItem> placedItems = [.. items.Values.Where (item => item.DefaultPlacement == placement)];
			if (placedItems.Count == 0)
				continue;

			root = WrapNode (root, new DockGroupNode (placedItems), placement, GetDefaultDockExtent (placedItems, placement));
		}
		return root;
	}

	private static int GetDefaultDockExtent (IEnumerable<DockItem> dockItems, DockPlacement placement)
	{
		bool horizontal = placement is DockPlacement.Left or DockPlacement.Right;
		Gtk.Orientation orientation = horizontal ? Gtk.Orientation.Horizontal : Gtk.Orientation.Vertical;
		int natural = 0;
		int itemCount = 0;
		bool onlyToolbox = true;
		foreach (DockItem item in dockItems) {
			item.Measure (orientation, -1, out _, out int itemNatural, out _, out _);
			natural = Math.Max (natural, itemNatural);
			itemCount++;
			onlyToolbox &= item.UniqueName == "Toolbox";
		}

		bool compactToolbox = placement == DockPlacement.Left && itemCount == 1 && onlyToolbox;
		int minimum = compactToolbox ? 88 : placement == DockPlacement.Left ? 124 : horizontal ? 260 : 160;
		int fallback = compactToolbox ? 88 : placement == DockPlacement.Left ? 124 : horizontal ? 320 : 240;
		int maximum = placement == DockPlacement.Left ? 260 : horizontal ? 420 : 320;
		return Math.Clamp (natural > 0 ? natural : fallback, minimum, maximum);
	}

	private static DockNode WrapNode (
		DockNode target,
		DockNode added,
		DockPlacement placement,
		int addedExtent)
	{
		return placement switch {
			DockPlacement.Left => new DockSplitNode (Gtk.Orientation.Horizontal, addedExtent, true, added, target),
			DockPlacement.Right => new DockSplitNode (Gtk.Orientation.Horizontal, addedExtent, false, target, added),
			DockPlacement.Top => new DockSplitNode (Gtk.Orientation.Vertical, addedExtent, true, added, target),
			DockPlacement.Bottom => new DockSplitNode (Gtk.Orientation.Vertical, addedExtent, false, target, added),
			_ => target,
		};
	}

	private static int GetDockExtent (DockItem item, DockPlacement placement, int width, int height)
	{
		bool horizontal = placement is DockPlacement.Left or DockPlacement.Right;
		int available = horizontal ? width : height;
		int current = horizontal ? item.GetWidth () : item.GetHeight ();
		int minimum = horizontal && item.UniqueName == "Toolbox" ? 88 : horizontal ? 124 : 120;
		int preferred = current > 0 ? current : GetDefaultDockExtent ([item], placement);
		int canvasMinimum = horizontal ? 320 : 240;
		int maximum = Math.Max (minimum, available - canvasMinimum);
		return available <= 0 ? preferred : Math.Clamp (preferred, minimum, maximum);
	}

	private void RemoveItemFromLayout (DockItem item)
	{
		DockGroupNode? group = FindGroup (item);
		group?.Items.Remove (item);
		if (group?.ActiveItem == item.UniqueName)
			group.ActiveItem = group.Items.FirstOrDefault ()?.UniqueName;
		PruneLayouts ();
	}

	private DockGroupNode? FindGroup (DockItem item)
	{
		DockGroupNode? group = main_root is null
			? null
			: EnumerateGroups (main_root).FirstOrDefault (candidate => candidate.Items.Contains (item));
		if (group is not null)
			return group;

		foreach (FloatingHost host in floating_hosts) {
			group = EnumerateGroups (host.Root).FirstOrDefault (candidate => candidate.Items.Contains (item));
			if (group is not null)
				return group;
		}
		return null;
	}

	private bool IsInFloatingWindow (DockItem item)
		=> floating_hosts.Any (host => ContainsItem (host.Root, item));

	private void PruneLayouts ()
	{
		main_root = PruneNode (main_root);
		for (int i = floating_hosts.Count - 1; i >= 0; i--) {
			FloatingHost host = floating_hosts[i];
			DockNode? root = PruneNode (host.Root);
			if (root is not null) {
				host.Root = root;
				continue;
			}

			host.Window.Destroy ();
			floating_hosts.RemoveAt (i);
		}
	}

	private static DockNode? PruneNode (DockNode? node)
	{
		if (node is DockGroupNode group)
			return group.Items.Count == 0 ? null : group;
		if (node is not DockSplitNode split)
			return node;

		split.First = PruneNode (split.First);
		split.Second = PruneNode (split.Second);
		return (split.First, split.Second) switch {
			(null, null) => null,
			(not null, null) => split.First,
			(null, not null) => split.Second,
			_ => split,
		};
	}

	private void ReplaceNodeInLayouts (DockNode target, DockNode replacement)
	{
		if (main_root is not null && ContainsNode (main_root, target)) {
			main_root = ReplaceNode (main_root, target, replacement);
			return;
		}

		foreach (FloatingHost host in floating_hosts) {
			if (!ContainsNode (host.Root, target))
				continue;
			host.Root = ReplaceNode (host.Root, target, replacement);
			return;
		}
	}

	private static DockNode ReplaceNode (DockNode node, DockNode target, DockNode replacement)
	{
		if (node == target)
			return replacement;
		if (node is DockSplitNode split) {
			if (split.First is not null)
				split.First = ReplaceNode (split.First, target, replacement);
			if (split.Second is not null)
				split.Second = ReplaceNode (split.Second, target, replacement);
		}
		return node;
	}

	private static bool ContainsNode (DockNode node, DockNode target)
		=> node == target || node is DockSplitNode split
			&& ((split.First is not null && ContainsNode (split.First, target))
				|| (split.Second is not null && ContainsNode (split.Second, target)));

	private void RemoveEmptyFloatingHosts ()
	{
		for (int i = floating_hosts.Count - 1; i >= 0; i--) {
			FloatingHost host = floating_hosts[i];
			if (host.Root is not null && EnumerateGroups (host.Root).Any (group => group.Items.Count > 0))
				continue;
			host.Window.Destroy ();
			floating_hosts.RemoveAt (i);
		}
	}

	private FloatingHost CreateFloatingHost (DockNode root, int width, int height)
	{
		Gtk.ApplicationWindow window = Gtk.ApplicationWindow.New (application);
		window.Title = Translations.GetString ("Pinta Tool Window");
		window.DefaultWidth = width;
		window.DefaultHeight = height;
		window.TransientFor = owner;
		window.DestroyWithParent = true;

		FloatingHost host = new (window, root);
		window.OnCloseRequest += (_, _) => {
			foreach (DockItem item in EnumerateGroups (host.Root).SelectMany (group => group.Items).Where (item => !item.Locked)) {
				if (hidden_items.Add (item.UniqueName))
					ItemVisibilityChanged?.Invoke (this, new (item.UniqueName, false));
			}
			Rebuild ();
			return true;
		};
		return host;
	}

	private void Rebuild ()
	{
		CaptureExtents (main_root);
		foreach (FloatingHost host in floating_hosts)
			CaptureExtents (host.Root);

		foreach (DockGroupView group in rendered_groups)
			group.DetachItems ();
		rendered_groups.Clear ();

		if (main_widget is not null) {
			Remove (main_widget);
			main_widget = null;
		}
		foreach (FloatingHost host in floating_hosts)
			host.Window.SetChild (null);

		main_widget = BuildWidget (main_root);
		if (main_widget is not null)
			Append (main_widget);

		foreach (FloatingHost host in floating_hosts) {
			Gtk.Widget? widget = BuildWidget (host.Root);
			if (widget is null || !tool_windows_visible) {
				host.Window.Hide ();
				continue;
			}

			host.Window.SetChild (widget);
			if (!host.Window.IsVisible ())
				host.Window.Present ();
		}
	}

	private Gtk.Widget? BuildWidget (DockNode? node)
	{
		if (node is null)
			return null;

		if (node is DockGroupNode group) {
			List<DockItem> visibleItems = [.. group.Items.Where (item => !hidden_items.Contains (item.UniqueName)
				&& (tool_windows_visible || item.Locked))];
			if (visibleItems.Count == 0)
				return null;

			DockGroupView view = new (this, group, visibleItems);
			rendered_groups.Add (view);
			return view.Widget;
		}

		DockSplitNode split = (DockSplitNode) node;
		split.Pane = null;
		Gtk.Widget? first = BuildWidget (split.First);
		Gtk.Widget? second = BuildWidget (split.Second);
		if (first is null)
			return second;
		if (second is null)
			return first;

		Gtk.Paned pane = Gtk.Paned.New (split.Orientation);
		pane.StartChild = first;
		pane.EndChild = second;
		pane.ResizeStartChild = !split.ExtentOnFirst;
		pane.ResizeEndChild = split.ExtentOnFirst;
		pane.ShrinkStartChild = false;
		pane.ShrinkEndChild = false;
		pane.WideHandle = true;
		split.Pane = pane;

		int remainingPasses = 2;
		GLib.Functions.TimeoutAdd (GLib.Constants.PRIORITY_DEFAULT, 40, () => {
			int length = split.Orientation == Gtk.Orientation.Horizontal ? pane.GetWidth () : pane.GetHeight ();
			if (length > 0) {
				int extent = Math.Clamp (split.Extent, 1, Math.Max (1, length - 1));
				pane.Position = split.ExtentOnFirst ? extent : length - extent;
			}

			return --remainingPasses > 0;
		});
		return pane;
	}

	private static void CaptureExtents (DockNode? node)
	{
		if (node is not DockSplitNode split)
			return;

		if (split.Pane is not null) {
			int length = split.Orientation == Gtk.Orientation.Horizontal ? split.Pane.GetWidth () : split.Pane.GetHeight ();
			if (length > 0)
				split.Extent = Math.Max (1, split.ExtentOnFirst ? split.Pane.Position : length - split.Pane.Position);
		}
		CaptureExtents (split.First);
		CaptureExtents (split.Second);
	}

	private static DockPlacement GetDropPlacement (double x, double y, int width, int height)
	{
		if (width <= 0 || height <= 0)
			return DockPlacement.Center;

		double horizontal = Math.Min (x, width - x);
		double vertical = Math.Min (y, height - y);
		if (Math.Min (horizontal, vertical) > 72)
			return DockPlacement.Center;
		if (horizontal < vertical)
			return x < width / 2.0 ? DockPlacement.Left : DockPlacement.Right;
		return y < height / 2.0 ? DockPlacement.Top : DockPlacement.Bottom;
	}

	private static IEnumerable<DockGroupNode> EnumerateGroups (DockNode node)
	{
		if (node is DockGroupNode group) {
			yield return group;
			yield break;
		}

		DockSplitNode split = (DockSplitNode) node;
		if (split.First is not null)
			foreach (DockGroupNode child in EnumerateGroups (split.First))
				yield return child;
		if (split.Second is not null)
			foreach (DockGroupNode child in EnumerateGroups (split.Second))
				yield return child;
	}

	private static bool ContainsItem (DockNode node, DockItem item)
		=> EnumerateGroups (node).Any (group => group.Items.Contains (item));

	private void NotifyAllVisibility ()
	{
		foreach (DockItem item in items.Values.Where (item => !item.Locked))
			ItemVisibilityChanged?.Invoke (this, new (item.UniqueName, !hidden_items.Contains (item.UniqueName)));
	}

	private static NodeState? SaveNode (DockNode? node)
		=> node switch {
			null => null,
			DockGroupNode group => new NodeState {
				Type = "group",
				Items = [.. group.Items.Select (item => item.UniqueName)],
				Active = group.ActiveItem,
			},
			DockSplitNode split => new NodeState {
				Type = "split",
				Orientation = split.Orientation == Gtk.Orientation.Horizontal ? "horizontal" : "vertical",
				Extent = split.Extent,
				ExtentOnFirst = split.ExtentOnFirst,
				First = SaveNode (split.First),
				Second = SaveNode (split.Second),
			},
			_ => null,
		};

	private DockNode? RestoreNode (NodeState? state, HashSet<string> used, bool allowLocked)
	{
		if (state is null)
			return null;
		if (state.Type == "group") {
			List<DockItem> restoredItems = [];
			foreach (string itemName in state.Items ?? []) {
				if (!items.TryGetValue (itemName, out DockItem? item))
					continue;
				if (!allowLocked && item.Locked)
					throw new InvalidDataException ("The canvas cannot float.");
				if (!used.Add (itemName))
					throw new InvalidDataException ("Dock layout contains duplicate items.");
				restoredItems.Add (item);
			}
			return restoredItems.Count == 0 ? null : new DockGroupNode (restoredItems) {
				ActiveItem = restoredItems.Any (item => item.UniqueName == state.Active) ? state.Active : restoredItems[0].UniqueName,
			};
		}

		if (state.Type != "split" || state.Extent is < 1 or > 16384)
			throw new InvalidDataException ("Dock layout contains an invalid node.");
		Gtk.Orientation orientation = state.Orientation switch {
			"horizontal" => Gtk.Orientation.Horizontal,
			"vertical" => Gtk.Orientation.Vertical,
			_ => throw new InvalidDataException ("Dock layout contains an invalid split orientation."),
		};
		DockNode? first = RestoreNode (state.First, used, allowLocked);
		DockNode? second = RestoreNode (state.Second, used, allowLocked);
		return (first, second) switch {
			(null, null) => null,
			(not null, null) => first,
			(null, not null) => second,
			_ => new DockSplitNode (orientation, state.Extent, state.ExtentOnFirst, first!, second!),
		};

	}

	private abstract class DockNode;

	private sealed class DockGroupNode (IEnumerable<DockItem> items) : DockNode
	{
		public List<DockItem> Items { get; } = [.. items];
		public string? ActiveItem { get; set; } = items.FirstOrDefault ()?.UniqueName;
	}

	private sealed class DockSplitNode (
		Gtk.Orientation orientation,
		int extent,
		bool extentOnFirst,
		DockNode first,
		DockNode second) : DockNode
	{
		public Gtk.Orientation Orientation { get; } = orientation;
		public int Extent { get; set; } = extent;
		public bool ExtentOnFirst { get; } = extentOnFirst;
		public DockNode? First { get; set; } = first;
		public DockNode? Second { get; set; } = second;
		public Gtk.Paned? Pane { get; set; }
	}

	private sealed class FloatingHost (Gtk.ApplicationWindow window, DockNode root)
	{
		public Gtk.ApplicationWindow Window { get; } = window;
		public DockNode Root { get; set; } = root;
	}

	private sealed class DockGroupView
	{
		private readonly Dock dock;
		private readonly DockGroupNode group;
		private readonly Gtk.Stack stack = Gtk.Stack.New ();
		private readonly List<DockItem> items;
		private readonly Gtk.Button drop_preview = Gtk.Button.New ();

		public DockGroupView (Dock dock, DockGroupNode group, List<DockItem> items)
		{
			this.dock = dock;
			this.group = group;
			this.items = items;

			DockItem active = items.FirstOrDefault (item => item.UniqueName == group.ActiveItem) ?? items[0];
			group.ActiveItem = active.UniqueName;

			Gtk.Box tabs = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
			tabs.AddCssClass (AdwaitaStyles.Toolbar);
			Gtk.ToggleButton? firstButton = null;
			foreach (DockItem item in items) {
				Gtk.ToggleButton button = CreateTabButton (item);
				if (firstButton is null)
					firstButton = button;
				else
					button.Group = firstButton;
				button.Active = item == active;
				button.OnClicked += (_, _) => Select (item);
				dock.ConfigureDragSource (button, item);
				tabs.Append (button);
				stack.AddNamed (item, item.UniqueName);
			}

			Gtk.Box spacer = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
			spacer.Hexpand = true;
			tabs.Append (spacer);
			bool standaloneToolbox = items.Count == 1 && items[0].UniqueName == "Toolbox";
			if (!items.Any (item => item.Locked) && !standaloneToolbox)
				tabs.Append (CreateMenuButton ());

			stack.VisibleChild = active;
			stack.Hexpand = true;
			stack.Vexpand = true;

			Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
			if (!items.Any (item => item.Locked))
				content.Append (tabs);
			content.Append (stack);

			Gtk.Overlay overlay = Gtk.Overlay.New ();
			overlay.Child = content;
			overlay.Hexpand = true;
			overlay.Vexpand = true;

			drop_preview.AddCssClass (AdwaitaStyles.SuggestedAction);
			drop_preview.CanTarget = false;
			drop_preview.Opacity = 0.32;
			drop_preview.Visible = false;
			overlay.AddOverlay (drop_preview);

			Gtk.DropTarget target = Gtk.DropTarget.New (GObject.Type.String, Gdk.DragAction.Move);
			target.OnEnter += (_, args) => UpdateDropPreview (args.X, args.Y, overlay.GetWidth (), overlay.GetHeight ());
			target.OnMotion += (_, args) => UpdateDropPreview (args.X, args.Y, overlay.GetWidth (), overlay.GetHeight ());
			target.OnLeave += (_, _) => HideDropPreview ();
			target.OnDrop += (_, args) => {
				HideDropPreview ();
				return args.Value.GetString () is string itemName
					&& dock.HandleDrop (group, itemName, args.X, args.Y, overlay.GetWidth (), overlay.GetHeight ());
			};
			overlay.AddController (target);
			Widget = overlay;
		}

		public Gtk.Widget Widget { get; }

		public void HideDropPreview () => drop_preview.Visible = false;

		public void DetachItems ()
		{
			foreach (DockItem item in items)
				stack.Remove (item);
		}

		private void Select (DockItem item)
		{
			group.ActiveItem = item.UniqueName;
			stack.VisibleChild = item;
		}

		private static Gtk.ToggleButton CreateTabButton (DockItem item)
		{
			Gtk.ToggleButton button = Gtk.ToggleButton.New ();
			button.AddCssClass (AdwaitaStyles.Flat);
			button.TooltipText = item.Label;
			button.SetChild (Gtk.Label.New (item.Label));
			return button;
		}

		private Gtk.MenuButton CreateMenuButton ()
		{
			Gtk.MenuButton menu = Gtk.MenuButton.New ();
			menu.IconName = Resources.StandardIcons.OpenMenu;
			menu.AddCssClass (AdwaitaStyles.Flat);

			Gtk.Popover popover = Gtk.Popover.New ();
			Gtk.Box actions = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
			actions.Append (CreateActionButton (Translations.GetString ("Dock Left"), () => DockActive (DockPlacement.Left), popover));
			actions.Append (CreateActionButton (Translations.GetString ("Dock Right"), () => DockActive (DockPlacement.Right), popover));
			actions.Append (CreateActionButton (Translations.GetString ("Dock Top"), () => DockActive (DockPlacement.Top), popover));
			actions.Append (CreateActionButton (Translations.GetString ("Dock Bottom"), () => DockActive (DockPlacement.Bottom), popover));
			actions.Append (CreateActionButton (Translations.GetString ("Float"), FloatActive, popover));
			actions.Append (CreateActionButton (Translations.GetString ("Close"), HideActive, popover));
			popover.Child = actions;
			menu.Popover = popover;
			return menu;
		}

		private static Gtk.Button CreateActionButton (string label, Action action, Gtk.Popover popover)
		{
			Gtk.Button button = Gtk.Button.NewWithLabel (label);
			button.Halign = Gtk.Align.Fill;
			button.AddCssClass (AdwaitaStyles.Flat);
			button.OnClicked += (_, _) => {
				popover.Popdown ();
				action ();
			};
			return button;
		}

		private void DockActive (DockPlacement placement)
		{
			if (group.ActiveItem is string name && dock.items.TryGetValue (name, out DockItem? item))
				dock.DockToMainEdge (item, placement);
		}

		private void FloatActive ()
		{
			if (group.ActiveItem is string name)
				dock.FloatItem (name);
		}

		private void HideActive ()
		{
			if (group.ActiveItem is string name)
				dock.SetItemVisible (name, false);
		}

		private Gdk.DragAction UpdateDropPreview (double x, double y, int width, int height)
		{
			if (dock.dragged_item is not DockItem draggedItem) {
				HideDropPreview ();
				return Gdk.DragAction.None;
			}

			DockPlacement placement = GetDropPlacement (x, y, width, height);
			if (placement == DockPlacement.Center && items.Any (item => item.Locked)) {
				HideDropPreview ();
				return Gdk.DragAction.Move;
			}

			const int margin = 8;
			int extent = Dock.GetDockExtent (draggedItem, placement, width, height);
			drop_preview.MarginStart = drop_preview.MarginEnd = margin;
			drop_preview.MarginTop = drop_preview.MarginBottom = margin;
			drop_preview.Hexpand = placement is DockPlacement.Center or DockPlacement.Top or DockPlacement.Bottom;
			drop_preview.Vexpand = placement is DockPlacement.Center or DockPlacement.Left or DockPlacement.Right;
			drop_preview.Halign = placement switch {
				DockPlacement.Left => Gtk.Align.Start,
				DockPlacement.Right => Gtk.Align.End,
				_ => Gtk.Align.Fill,
			};
			drop_preview.Valign = placement switch {
				DockPlacement.Top => Gtk.Align.Start,
				DockPlacement.Bottom => Gtk.Align.End,
				_ => Gtk.Align.Fill,
			};
			drop_preview.SetSizeRequest (
				placement is DockPlacement.Left or DockPlacement.Right ? Math.Max (1, extent - 2 * margin) : -1,
				placement is DockPlacement.Top or DockPlacement.Bottom ? Math.Max (1, extent - 2 * margin) : -1);
			drop_preview.Visible = true;
			return Gdk.DragAction.Move;
		}
	}

	private sealed class LayoutState
	{
		public int Version { get; set; }
		public NodeState? Main { get; set; }
		public List<FloatingState>? Floating { get; set; }
		public List<string>? Hidden { get; set; }
	}

	private sealed class FloatingState
	{
		public NodeState? Root { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
	}

	private sealed class NodeState
	{
		public string Type { get; set; } = string.Empty;
		public string? Orientation { get; set; }
		public int Extent { get; set; }
		public bool ExtentOnFirst { get; set; }
		public List<string>? Items { get; set; }
		public string? Active { get; set; }
		public NodeState? First { get; set; }
		public NodeState? Second { get; set; }
	}
}
