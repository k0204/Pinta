//
// LayersPad.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//
// Copyright (c) 2011 Jonathan Pobst
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
using Pinta.Core;
using Pinta.Docking;
using Pinta.Gui.Widgets;

namespace Pinta;

internal sealed class LayersPad : IDockPad
{
	private readonly LayerActions layer_actions;
	private readonly VideoFrameExportAction video_frame_export;
	private Gtk.SpinButton opacity_spinner = null!;
	private bool updating_opacity;

	internal LayersPad (LayerActions layerActions, VideoFrameExportAction videoFrameExport)
	{
		layer_actions = layerActions;
		video_frame_export = videoFrameExport;
	}

	public void Initialize (Dock workspace)
	{
		LayersListView layers = LayersListView.New ();
		layers.Vexpand = true;

		Gtk.Label opacityLabel = Gtk.Label.New (Translations.GetString ("Opacity:"));
		opacityLabel.Halign = Gtk.Align.Start;

		opacity_spinner = Gtk.SpinButton.NewWithRange (0, 100, 1);
		opacity_spinner.Adjustment!.PageIncrement = 10;
		opacity_spinner.ClimbRate = 1;
		opacity_spinner.Value = 100;
		opacity_spinner.WidthRequest = 84;
		opacity_spinner.OnValueChanged += HandleOpacityChanged;

		Gtk.Box opacityRow = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		opacityRow.MarginTop = 6;
		opacityRow.MarginBottom = 6;
		opacityRow.MarginStart = 6;
		opacityRow.MarginEnd = 6;
		opacityRow.Append (opacityLabel);
		opacityRow.Append (opacity_spinner);
		opacityRow.Append (Gtk.Label.New ("%"));

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		Gtk.Button actionGenerationButton = layer_actions.GenerateSingleDirectionAnimation.CreateDockToolBarItem ();
		actionGenerationButton.Label = Translations.GetString ("Generate Action Animation");
		actionGenerationButton.Halign = Gtk.Align.Fill;
		actionGenerationButton.MarginStart = 6;
		actionGenerationButton.MarginEnd = 6;
		actionGenerationButton.MarginTop = 6;
		actionGenerationButton.SetTooltipText (Translations.GetString ("Generate a single-direction animation with action prompts"));
		actionGenerationButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		content.Append (actionGenerationButton);
		content.Append (opacityRow);
		content.Append (layers);
		layers.VideoEditorRequested += HandleVideoEditorClicked;

		DockItem layers_item = DockItem.New (
			child: content,
			uniqueName: "Layers",
			iconName: Resources.Icons.LayerDuplicate
		);
		layers_item.Label = Translations.GetString ("Layers");

		Gio.Menu hamburger_menu = Gio.Menu.New ();

		Gio.Menu flip_section = Gio.Menu.New ();
		flip_section.AppendItem (layer_actions.FlipHorizontal.CreateMenuItem ());
		flip_section.AppendItem (layer_actions.FlipVertical.CreateMenuItem ());
		flip_section.AppendItem (layer_actions.ResizeLayer.CreateMenuItem ());
		flip_section.AppendItem (layer_actions.RotateZoom.CreateMenuItem ());

		Gio.Menu alignment_menu = Gio.Menu.New ();
		alignment_menu.AppendItem (layer_actions.AlignLayersLeft.CreateMenuItem ());
		alignment_menu.AppendItem (layer_actions.AlignLayersCenterHorizontal.CreateMenuItem ());
		alignment_menu.AppendItem (layer_actions.AlignLayersRight.CreateMenuItem ());
		alignment_menu.AppendItem (layer_actions.AlignLayersTop.CreateMenuItem ());
		alignment_menu.AppendItem (layer_actions.AlignLayersCenterVertical.CreateMenuItem ());
		alignment_menu.AppendItem (layer_actions.AlignLayersBottom.CreateMenuItem ());

		Gio.Menu prop_section = Gio.Menu.New ();
		prop_section.AppendItem (layer_actions.Properties.CreateMenuItem ());

		hamburger_menu.AppendItem (layer_actions.ImportFromFile.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.GenerateSpritesheet.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.GenerateSingleDirectionAnimation.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.CreateMultiDirectionAnimation.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.ImageSplit.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.AutoSplit.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.CreateSingleDirectionAnimation.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.EditSpritesheet.CreateMenuItem ());
		hamburger_menu.AppendItem (layer_actions.SetSpritesheetAnchor.CreateMenuItem ());
		hamburger_menu.AppendSubmenu (Translations.GetString ("Align Layers"), alignment_menu);
		hamburger_menu.AppendSection (null, flip_section);
		hamburger_menu.AppendSection (null, prop_section);

		Gtk.MenuButton hamburger_button = GtkExtensions.CreateMenuButton (
			hamburger_menu, Resources.StandardIcons.OpenMenu);

		hamburger_button.Direction = Gtk.ArrowType.Up;

		Gtk.Box layers_tb = layers_item.AddToolBar ();
		layers_tb.AppendMultiple ([
			layer_actions.AddNewLayer.CreateDockToolBarItem (),
                        layer_actions.AddNewGroup.CreateDockToolBarItem (),
			layer_actions.AddVideoLayer.CreateDockToolBarItem (),
			layer_actions.GenerateImage.CreateDockToolBarItem (),
			layer_actions.GenerateSpritesheet.CreateDockToolBarItem (),
			layer_actions.GenerateSingleDirectionAnimation.CreateDockToolBarItem (),
			layer_actions.CreateMultiDirectionAnimation.CreateDockToolBarItem (),
			layer_actions.ImageSplit.CreateDockToolBarItem (),
			layer_actions.CreateSingleDirectionAnimation.CreateDockToolBarItem (),
			layer_actions.EditSpritesheet.CreateDockToolBarItem (),
			layer_actions.DeleteLayer.CreateDockToolBarItem (),
			layer_actions.DuplicateLayer.CreateDockToolBarItem (),
			layer_actions.UnlockReference.CreateDockToolBarItem (),
			layer_actions.MergeLayerDown.CreateDockToolBarItem (),
			layer_actions.MoveLayerUp.CreateDockToolBarItem (),
			layer_actions.MoveLayerDown.CreateDockToolBarItem (),
			hamburger_button
		]);

		workspace.AddItem (layers_item, DockPlacement.Right);

		PintaCore.Workspace.ActiveDocumentChanged += HandleOpacityTargetChanged;
		PintaCore.Workspace.SelectedLayerChanged += HandleOpacityTargetChanged;
		PintaCore.Workspace.LayerPropertyChanged += HandleOpacityTargetChanged;
		video_frame_export.VideoImported += HandleVideoImported;
		UpdateOpacityControl ();
	}

	private void HandleVideoEditorClicked (object? sender, EventArgs args)
	{
		if (PintaCore.Workspace.ActiveDocumentOrDefault is Document document
			&& document.Layers.HasSelectedLayer
			&& document.Layers.CurrentUserLayer is VideoEditingLayer layer) {
			if (string.IsNullOrWhiteSpace (layer.VideoPath))
				video_frame_export.ImportVideoForLayer (layer);
			else
				video_frame_export.EditVideoLayer (layer);
		}
	}

	private void HandleVideoImported (object? sender, EventArgs args)
	{
		if (PintaCore.Workspace.ActiveDocumentOrDefault is Document document) {
			document.IsDirty = true;
			document.Layers.NotifyLayerTreeChanged ();
		}
	}

	private void HandleOpacityChanged (object? sender, EventArgs e)
	{
		if (updating_opacity || !PintaCore.Workspace.HasOpenDocuments)
			return;

		Document document = PintaCore.Workspace.ActiveDocument;
		UserLayer layer = document.Layers.CurrentUserLayer;
		double opacity = opacity_spinner.Value / 100d;
		if (layer.Opacity == opacity)
			return;

		LayerProperties initial = new (layer.Name, layer.Hidden, layer.Opacity, layer.BlendMode);
		LayerProperties updated = initial with { Opacity = opacity };
		layer.Opacity = opacity;
		if (document.Layers.ShowSelectionLayer)
			document.Layers.SelectionLayer.Opacity = opacity;

		document.History.PushNewItem (new UpdateLayerPropertiesHistoryItem (
			Resources.Icons.LayerProperties,
			Translations.GetString ("Layer Opacity"),
			layer,
			initial,
			updated));
		document.Workspace.Invalidate ();
	}

	private void HandleOpacityTargetChanged (object? sender, EventArgs e)
		=> UpdateOpacityControl ();

	private void UpdateOpacityControl ()
	{
		updating_opacity = true;
		bool hasLayer = PintaCore.Workspace.HasOpenDocuments && PintaCore.Workspace.ActiveDocument.Layers.HasSelectedLayer;
		opacity_spinner.Sensitive = hasLayer;
		opacity_spinner.Value = hasLayer
			? Math.Round (PintaCore.Workspace.ActiveDocument.Layers.CurrentUserLayer.Opacity * 100)
			: 100;
		updating_opacity = false;
	}
}
