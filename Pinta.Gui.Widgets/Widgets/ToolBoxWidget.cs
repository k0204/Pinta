using System;
using System.Collections.Generic;
using System.Linq;
using Pinta.Core;

namespace Pinta.Gui.Widgets;

[GObject.Subclass<Gtk.FlowBox>]
public sealed partial class ToolBoxWidget
{
	private ToolManager tools = null!; // NRT - set in factory method
	private readonly Dictionary<BaseTool, ToolGroupWidget> tool_groups = new ();
	private readonly Dictionary<string, ToolGroupWidget> groups_by_key = new ();
	// Dummy ToggleButton to use for grouping together the tools' buttons.
	private readonly Gtk.ToggleButton toggle_group = Gtk.ToggleButton.New ();

	partial void Initialize ()
	{
		SetOrientation (Gtk.Orientation.Vertical);
		MinChildrenPerLine = 8; // Pinta 3 has 22 default tools, meaning a max of 3 columns regardless of size, smaller values don't lead to better use of visual space.
		MaxChildrenPerLine = 1024; // Allow for single column if there's sufficient space to do so.
		SelectionMode = Gtk.SelectionMode.None; // Don't allow the buttons to be selected.
	}

	public static ToolBoxWidget New (ToolManager tools)
	{
		ToolBoxWidget widget = NewWithProperties ([]);
		widget.Configure (tools);
		return widget;
	}

	private void Configure (ToolManager tools)
	{
		tools.ToolAdded += (_, e) => HandleToolAdded (e.Tool);
		tools.ToolRemoved += (_, e) => HandleToolRemoved (e.Tool);
		tools.ToolActivated += (_, e) => HandleToolActivated (e.Tool);
		tools.ToolShortcutsChanged += (_, _) => RebuildTools ();

		this.tools = tools;

		foreach (BaseTool tool in tools)
			HandleToolAdded (tool);
	}

	private void RebuildTools ()
	{
		foreach (ToolGroupWidget group in groups_by_key.Values)
			Remove (group.Button);

		tool_groups.Clear ();
		groups_by_key.Clear ();

		foreach (BaseTool tool in tools)
			HandleToolAdded (tool);
	}

	private void HandleToolAdded (BaseTool tool)
	{
		string key = GetToolGroupKey (tool);

		if (!groups_by_key.TryGetValue (key, out ToolGroupWidget? group)) {
			group = new (key, tools, toggle_group);
			groups_by_key.Add (key, group);
			Insert (group.Button, GetGroupInsertIndex (tool));
		}

		group.AddTool (tool);
		tool_groups[tool] = group;

		if (tools.CurrentTool is BaseTool currentTool && tool_groups.TryGetValue (currentTool, out ToolGroupWidget? currentGroup)) {
			currentGroup.SetCurrentTool (currentTool);
			currentGroup.Button.Active = true;
		}
	}

	/// <summary>
	/// If the tool was switched without clicking on the button (e.g. via shortcut key),
	/// ensure the tool's button is active. Note we don't need to deactivate the previous
	/// button since they're all in the same toggle button group.
	/// </summary>
	private void HandleToolActivated (BaseTool tool)
	{
		ToolGroupWidget group = tool_groups[tool];
		group.SetCurrentTool (tool);
		group.Button.Active = true;
	}

	private void HandleToolRemoved (BaseTool tool)
	{
		ToolGroupWidget group = tool_groups[tool];
		tool_groups.Remove (tool);
		group.RemoveTool (tool);

		if (group.Count > 0)
			return;

		Remove (group.Button);
		groups_by_key.Remove (group.Key);
	}

	private int GetGroupInsertIndex (BaseTool tool)
	{
		List<BaseTool> orderedTools = tools.ToList ();
		int toolIndex = orderedTools.IndexOf (tool);
		return orderedTools
			.Take (toolIndex)
			.Select (GetToolGroupKey)
			.Distinct ()
			.Count ();
	}

	private string GetToolGroupKey (BaseTool tool)
	{
		Gdk.Key shortcut = tools.GetShortcut (tool);
		if (shortcut == Gdk.Key.Invalid)
			return $"tool:{tool.GetType ().Name}";

		return $"shortcut:{shortcut.ToUpper ().Name ()}";
	}

	private static string BuildToolTooltip (
		ToolManager tools,
		BaseTool tool,
		IReadOnlyCollection<BaseTool> groupedTools)
	{
		List<string> lines = [tool.Name];

		Gdk.Key shortcut = tools.GetShortcut (tool);
		if (shortcut != Gdk.Key.Invalid) {
			string shortcutLabel = Translations.GetString ("Shortcut key");
			lines.Add ($"{shortcutLabel}: {shortcut.ToUpper ().Name ()}");
		}

		if (!string.IsNullOrWhiteSpace (tool.StatusBarText))
			lines.Add (tool.StatusBarText);

		if (groupedTools.Count > 1)
			lines.AddRange (groupedTools.Select (t => $"* {t.Name}"));

		return string.Join ('\n', lines);
	}

	private sealed class ToolGroupWidget
	{
		private readonly string key;
		private readonly ToolManager tool_manager;
		private readonly Gtk.Image icon = Gtk.Image.New ();
		private readonly Gtk.Image group_indicator = Gtk.Image.NewFromIconName ("pan-down-symbolic");
		private readonly Gtk.Box popover_box = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		private readonly Gtk.Popover popover = Gtk.Popover.New ();
		private readonly List<BaseTool> grouped_tools = [];

		private BaseTool? current_tool;

		public ToolGroupWidget (string key, ToolManager toolManager, Gtk.ToggleButton toggleGroup)
		{
			this.key = key;
			tool_manager = toolManager;

			Gtk.Overlay overlay = Gtk.Overlay.New ();
			overlay.Child = icon;
			overlay.AddOverlay (group_indicator);

			group_indicator.Halign = Gtk.Align.End;
			group_indicator.Valign = Gtk.Align.End;
			group_indicator.MarginEnd = 1;
			group_indicator.MarginBottom = 1;
			group_indicator.PixelSize = 8;
			group_indicator.Visible = false;

			Button = Gtk.ToggleButton.New ();
			Button.Group = toggleGroup;
			Button.CanFocus = false;
			Button.SetCssClasses ([Resources.Styles.ToolBoxButton, AdwaitaStyles.Flat]);
			Button.SetChild (overlay);
			Button.OnClicked += (_, _) => {
				if (current_tool is null)
					return;

				if (grouped_tools.Count > 1 && tool_manager.CurrentTool == current_tool) {
					popover.Popup ();
					return;
				}

				tool_manager.SetCurrentTool (current_tool);
			};

			Gtk.GestureClick secondaryClick = Gtk.GestureClick.New ();
			secondaryClick.SetButton (Gdk.Constants.BUTTON_SECONDARY);
			secondaryClick.OnPressed += (_, _) => {
				if (grouped_tools.Count > 1)
					popover.Popup ();
			};
			Button.AddController (secondaryClick);

			popover.Position = Gtk.PositionType.Right;
			popover.SetParent (Button);
			popover.Child = popover_box;
		}

		public Gtk.ToggleButton Button { get; }

		public int Count => grouped_tools.Count;

		public string Key => key;

		public void AddTool (BaseTool tool)
		{
			grouped_tools.Add (tool);
			current_tool ??= tool;
			UpdateDisplay ();
			RebuildPopover ();
		}

		public void RemoveTool (BaseTool tool)
		{
			grouped_tools.Remove (tool);

			if (current_tool == tool)
				current_tool = grouped_tools.FirstOrDefault ();

			if (grouped_tools.Count == 0) {
				popover.Popdown ();
				return;
			}

			UpdateDisplay ();
			RebuildPopover ();
		}

		public void SetCurrentTool (BaseTool tool)
		{
			if (!grouped_tools.Contains (tool))
				return;

			current_tool = tool;
			UpdateDisplay ();
			RebuildPopover ();
		}

		private void RebuildPopover ()
		{
			popover_box.RemoveAll ();

			foreach (BaseTool tool in grouped_tools) {
				Gtk.Button rowButton = Gtk.Button.New ();
				rowButton.SetCssClasses ([AdwaitaStyles.Flat]);
				rowButton.OnClicked += (_, _) => {
					SetCurrentTool (tool);
					tool_manager.SetCurrentTool (tool);
					popover.Popdown ();
				};

				Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
				Gtk.Image rowIcon = Gtk.Image.NewFromIconName (tool.Icon);
				Gtk.Label rowLabel = Gtk.Label.New (tool.Name);
				rowLabel.Halign = Gtk.Align.Start;
				rowLabel.Hexpand = true;
				Gtk.Image selectedIcon = Gtk.Image.NewFromIconName (Resources.StandardIcons.ObjectSelect);
				selectedIcon.Visible = tool == current_tool;

				row.Append (rowIcon);
				row.Append (rowLabel);
				row.Append (selectedIcon);

				rowButton.SetChild (row);
				popover_box.Append (rowButton);
			}

			group_indicator.Visible = grouped_tools.Count > 1;
		}

		private void UpdateDisplay ()
		{
			if (current_tool is null)
				return;

			icon.IconName = current_tool.Icon;
			Button.Name = current_tool.Name;
			Button.TooltipText = BuildToolTooltip (tool_manager, current_tool, grouped_tools);
		}
	}
}
