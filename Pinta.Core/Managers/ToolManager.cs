//
// ToolManager.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
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
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace Pinta.Core;

public interface IToolService
{
	/// <summary>
	/// Adds a new tool to the tool box.
	/// </summary>
	void AddTool (BaseTool tool);

	/// <summary>
	/// Instructs the current tool to commit any work that is in a temporary state.
	/// </summary>
	void Commit ();

	/// <summary>
	/// Gets the currently selected tool.
	/// </summary>
	BaseTool? CurrentTool { get; }

	/// <summary>
	/// Performs the mouse down event for the currently selected tool.
	/// </summary>
	void DoMouseDown (Document document, ToolMouseEventArgs e);

	/// <summary>
	/// Gets the previously selected tool.
	/// </summary>
	BaseTool? PreviousTool { get; }

	/// <summary>
	/// Removes the first found tool of the specified type from tool box.
	/// </summary>
	void RemoveInstanceOfTool<T> () where T : BaseTool;

	/// <summary>
	/// Sets the current tool to the specified tool.
	/// </summary>
	void SetCurrentTool (BaseTool tool);

	/// <summary>
	/// Sets the current tool to the first tool with the specified tool type name, like
	/// 'PencilTool'. Returns a value indicating if tool was successfully changed.
	/// </summary>
	bool SetCurrentTool (string tool);

	/// <summary>
	/// Sets the current tool to the next tool with the specified shortcut.
	/// </summary>
	bool SetCurrentTool (Gdk.Key shortcut);
}

public sealed class ToolManager : IEnumerable<BaseTool>, IToolService
{
	private readonly SortedSet<BaseTool> tools = new (new ToolSorter ());

	private readonly WorkspaceManager workspace_manager;
	private readonly ChromeManager chrome_manager;
	private readonly ShortcutManager shortcut_manager;
	public ToolManager (WorkspaceManager workspaceManager, ChromeManager chromeManager, ShortcutManager shortcutManager)
	{
		workspace_manager = workspaceManager;
		chrome_manager = chromeManager;
		shortcut_manager = shortcutManager;
		shortcut_manager.ToolShortcutsChanged += (_, _) => ToolShortcutsChanged?.Invoke (this, EventArgs.Empty);

		// Before the active document has changed, the current tool should commit unfinished changes.
		workspace_manager.PreActiveDocumentChanged += (_, _) => Commit ();
	}

	private bool is_panning;
	private bool is_space_pressed;
	private MouseButton pan_mouse_button;
	private Gdk.Cursor? stored_cursor;
	private bool has_stored_cursor;

	public event EventHandler<ToolEventArgs>? ToolAdded;
	public event EventHandler<ToolEventArgs>? ToolRemoved;
	public event EventHandler<ToolEventArgs>? ToolActivated;
	public event EventHandler? ToolShortcutsChanged;

	public BaseTool? CurrentTool { get; private set; }

	public BaseTool? PreviousTool { get; private set; }

	public void AddTool (BaseTool tool)
	{
		if (!tools.Add (tool))
			throw new Exception ("Attempted to add a duplicate tool");

		ToolAdded?.Invoke (this, new ToolEventArgs (tool));

		if (CurrentTool is null)
			SetCurrentTool (tool);
	}

	public void RemoveInstanceOfTool<T> () where T : BaseTool
	{
		T? tool =
			tools.OfType<T> ()
			.FirstOrDefault ();

		if (tool is null)
			return;

		if (!tools.Remove (tool))
			throw new Exception ("Attempted to remove a tool that wasn't registered");

		// Are we trying to remove the current tool?
		if (CurrentTool == tool) {
			// Can we set it back to the previous tool?
			if (PreviousTool is not null && PreviousTool != CurrentTool)
				SetCurrentTool (PreviousTool);
			else if (tools.Count != 0)  // Any tool?
				SetCurrentTool (tools.First ());
			else {
				// There are no tools left.
				DeactivateTool (tool, null);
				PreviousTool = null;
				CurrentTool = null;
			}
		}

		ToolRemoved?.Invoke (this, new ToolEventArgs (tool));
	}

	private BaseTool? FindTool (string name)
	{
		return tools.FirstOrDefault (t => string.Compare (name, t.GetType ().Name, true) == 0);
	}

	public void Commit ()
	{
		CurrentTool?.DoCommit (workspace_manager.ActiveDocumentOrDefault);
	}

	public void SetCurrentTool (BaseTool tool)
	{
		// Bail if this is already the current tool
		if (CurrentTool == tool)
			return;

		// Unload previous tool if needed
		if (CurrentTool is not null) {
			PreviousTool = CurrentTool;
			DeactivateTool (PreviousTool, tool);
		}

		// Load new tool
		CurrentTool = tool;

		tool.DoActivated (workspace_manager.ActiveDocumentOrDefault);

		ToolImage.SetFromIconName (tool.Icon);

		chrome_manager.ToolToolBar.Append (ToolLabel);
		chrome_manager.ToolToolBar.Append (ToolImage);
		chrome_manager.ToolToolBar.Append (ToolSeparator);

		chrome_manager.ToolToolBar.Append (ToolWidgetsScroll);
		tool.DoBuildToolBar (ToolWidgetsBox);

		workspace_manager.Invalidate ();
		chrome_manager.SetStatusBarText ($" {tool.Name}: {tool.StatusBarText}");

		ToolActivated?.Invoke (this, new ToolEventArgs (tool));
	}

	public bool SetCurrentTool (string tool)
	{
		if (FindTool (tool) is not BaseTool t)
			return false;

		SetCurrentTool (t);
		return true;
	}

	public bool SetCurrentTool (Gdk.Key shortcut)
	{
		if (FindNextTool (shortcut) is not BaseTool tool)
			return false;

		SetCurrentTool (tool);
		return true;
	}

	private BaseTool? FindNextTool (Gdk.Key shortcut)
	{
		// Find all tools with this shortcut
		var shortcut_tools =
			tools
			.Where (t => GetShortcut (t).ToUpper () == shortcut.ToUpper ())
			.ToImmutableArray ();

		// No tools with this shortcut, bail
		if (shortcut_tools.Length == 0)
			return null;

		// Only one option, return it
		if (shortcut_tools.Length == 1 || CurrentTool is null)
			return shortcut_tools.First ();

		// Get the tool after the currently selected tool
		int next_index = shortcut_tools.IndexOf (CurrentTool) + 1;

		// Wrap if we're past the final tool
		if (next_index >= shortcut_tools.Length)
			next_index = 0;

		return shortcut_tools[next_index];
	}

	private void DeactivateTool (BaseTool tool, BaseTool? newTool)
	{
		ToolWidgetsBox.RemoveAll ();
		chrome_manager.ToolToolBar.RemoveAll ();

		tool.DoDeactivated (workspace_manager.ActiveDocumentOrDefault, newTool);
	}

	public void DoMouseDown (Document document, ToolMouseEventArgs args)
	{
		if (TryMouseDownPanOverride (document, args))
			return;

		if (CurrentTool?.RequiresEditableLayer == true && !document.Layers.CurrentUserLayer.IsEditable)
			return;

		CurrentTool?.DoMouseDown (document, args);
	}

	public void DoMouseMove (Document document, ToolMouseEventArgs args)
	{
		if (!TryMouseMovePanOverride (document, args))
			CurrentTool?.DoMouseMove (document, args);
	}

	public void DoMouseUp (Document document, ToolMouseEventArgs args)
	{
		if (!TryMouseUpPanOverride (document, args))
			CurrentTool?.DoMouseUp (document, args);
	}

	public bool DoKeyDown (Document document, ToolKeyEventArgs args)
	{
		if (args.Key.Value != Gdk.Constants.KEY_space)
			return CurrentTool?.DoKeyDown (document, args) ?? false;

		is_space_pressed = true;
		if (TryGetPanTool (out BaseTool? pan))
			SetPanCursor (document, pan);

		return true;
	}

	public Gdk.Key GetShortcut (BaseTool tool)
		=> shortcut_manager.GetToolShortcut (tool);

	public bool DoKeyUp (Document document, ToolKeyEventArgs args)
	{
		if (args.Key.Value != Gdk.Constants.KEY_space)
			return CurrentTool?.DoKeyUp (document, args) ?? false;

		is_space_pressed = false;
		if (!is_panning)
			RestoreCursor (document);

		return true;
	}

	public void DoAfterSave (Document document)
		=> CurrentTool?.DoAfterSave (document);

	public Task<bool> DoHandlePaste (Document document, Gdk.Clipboard clipboard)
		=> CurrentTool?.DoHandlePaste (document, clipboard) ?? Task.FromResult (false);

	public IEnumerator<BaseTool> GetEnumerator ()
		=> tools.GetEnumerator ();

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator ()
		=> tools.GetEnumerator ();

	private bool TryMouseDownPanOverride (Document document, ToolMouseEventArgs args)
	{
		if (is_panning)
			return true;

		bool shouldPan = args.MouseButton == MouseButton.Middle
			|| (is_space_pressed && args.MouseButton == MouseButton.Left);
		if (!shouldPan || !TryGetPanTool (out BaseTool? pan))
			return false;

		is_panning = true;
		pan_mouse_button = args.MouseButton;
		SetPanCursor (document, pan);
		pan.DoMouseDown (document, args);
		return true;
	}

	private bool TryMouseMovePanOverride (Document document, ToolMouseEventArgs args)
	{
		if (!is_panning || !TryGetPanTool (out var pan))
			return false;

		pan.DoMouseMove (document, args);
		return true;
	}

	private bool TryMouseUpPanOverride (Document document, ToolMouseEventArgs args)
	{
		if (!is_panning || !TryGetPanTool (out var pan))
			return false;

		if (args.MouseButton != pan_mouse_button)
			return true;

		is_panning = false;
		pan_mouse_button = MouseButton.None;
		pan.DoMouseUp (document, args);
		if (!is_space_pressed)
			RestoreCursor (document);
		return true;
	}

	private void SetPanCursor (Document document, BaseTool pan)
	{
		if (!has_stored_cursor) {
			stored_cursor = document.Workspace.Canvas.Cursor;
			has_stored_cursor = true;
		}

		document.Workspace.Canvas.Cursor = pan.DefaultCursor;
		document.Workspace.CanvasContainer.Cursor = pan.DefaultCursor;
	}

	private void RestoreCursor (Document document)
	{
		if (!has_stored_cursor)
			return;

		document.Workspace.Canvas.Cursor = stored_cursor;
		document.Workspace.CanvasContainer.Cursor = null;
		stored_cursor = null;
		has_stored_cursor = false;
	}

	private bool TryGetPanTool ([NotNullWhen (true)] out BaseTool? tool)
	{
		tool = FindTool ("PanTool");

		return tool is not null;
	}

	private sealed class ToolSorter : Comparer<BaseTool>
	{
		public override int Compare (BaseTool? x, BaseTool? y)
		{
			int result = (x?.Priority ?? 0) - (y?.Priority ?? 0);

			if (result != 0)
				return result;

			// If two tools have the same priority, sort by type name so that both tools can still
			// be inserted into the set.
			string x_type = x?.GetType ().AssemblyQualifiedName ?? string.Empty;
			string y_type = y?.GetType ().AssemblyQualifiedName ?? string.Empty;
			return x_type.CompareTo (y_type);
		}
	}

	private Gtk.Label? tool_label;
	private Gtk.Image? tool_image;
	private Gtk.Separator? tool_sep;
	private Gtk.Box? tool_widgets_box;
	private Gtk.ScrolledWindow? tool_widgets_scroll;

	private Gtk.Label ToolLabel => tool_label ??= Gtk.Label.New (string.Format (" {0}:  ", Translations.GetString ("Tool")));
	private Gtk.Image ToolImage => tool_image ??= Gtk.Image.New ();
	private Gtk.Separator ToolSeparator => tool_sep ??= GtkExtensions.CreateToolBarSeparator ();
	private Gtk.Box ToolWidgetsBox => tool_widgets_box ??= Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
	// Scroll the toolbar contents if they are very long (e.g. the line/curve tool).
	private Gtk.ScrolledWindow ToolWidgetsScroll {
		get {
			if (tool_widgets_scroll == null) {
				tool_widgets_scroll = Gtk.ScrolledWindow.New ();
				tool_widgets_scroll.Child = ToolWidgetsBox;
				tool_widgets_scroll.HscrollbarPolicy = Gtk.PolicyType.Automatic;
				tool_widgets_scroll.VscrollbarPolicy = Gtk.PolicyType.Never;
				tool_widgets_scroll.HasFrame = false;
				tool_widgets_scroll.OverlayScrolling = true;
				tool_widgets_scroll.WindowPlacement = Gtk.CornerType.BottomRight;
				tool_widgets_scroll.Hexpand = true;
				tool_widgets_scroll.Halign = Gtk.Align.Fill;
			}

			return tool_widgets_scroll;
		}
	}
}
