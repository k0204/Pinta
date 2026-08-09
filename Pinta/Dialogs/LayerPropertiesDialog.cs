//
// LayerPropertiesDialog.cs
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
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Pinta.Core;

namespace Pinta;

[GObject.Subclass<PintaDialog>]
public sealed partial class LayerPropertiesDialog
{
	private LayerProperties initial_properties = new (string.Empty, false, 0.0, BlendMode.Normal);

	private double current_layer_opacity;
	private bool current_layer_hidden;
	private string current_layer_name = string.Empty;
	private BlendMode current_layer_blend_mode;

	private Gtk.Entry layer_name_entry;
	private Gtk.CheckButton visibility_checkbox;
	private Gtk.SpinButton opacity_spinner;
	private Gtk.Scale opacity_slider;
	private Gtk.ComboBoxText blend_combo_box;

	private Document document = null!; // NRT - set by factory method
	private UserLayer layer = null!; // NRT - set by factory method

	[MemberNotNull (nameof (layer_name_entry))]
	[MemberNotNull (nameof (visibility_checkbox))]
	[MemberNotNull (nameof (opacity_slider))]
	[MemberNotNull (nameof (opacity_spinner))]
	[MemberNotNull (nameof (blend_combo_box))]
	partial void Initialize ()
	{
		const int spacing = 6;

		Gtk.Label nameLabel = Gtk.Label.New (Translations.GetString ("Name:"));
		nameLabel.Halign = Gtk.Align.End;

		Gtk.Entry layerNameEntry = Gtk.Entry.New ();
		layerNameEntry.Hexpand = true;
		layerNameEntry.Halign = Gtk.Align.Fill;
		layerNameEntry.OnChanged += OnLayerNameChanged;
		layerNameEntry.SetActivatesDefault (true);

		Gtk.CheckButton visibilityCheckbox = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Visible"));
		visibilityCheckbox.OnToggled += OnVisibilityToggled;

		Gtk.Label blendLabel = Gtk.Label.New (Translations.GetString ("Blend Mode") + ":");
		blendLabel.Halign = Gtk.Align.End;

		Gtk.ComboBoxText blendComboBox = Gtk.ComboBoxText.New ();

		foreach (string name in UserBlendOps.GetAllBlendModeNames ())
			blendComboBox.AppendText (name);

		blendComboBox.Hexpand = true;
		blendComboBox.Halign = Gtk.Align.Fill;
		blendComboBox.OnChanged += OnBlendModeChanged;

		Gtk.Label opacityLabel = Gtk.Label.New (Translations.GetString ("Opacity:"));
		opacityLabel.Halign = Gtk.Align.End;

		Gtk.SpinButton opacitySpinner = Gtk.SpinButton.NewWithRange (0, 100, 1);
		opacitySpinner.Adjustment!.PageIncrement = 10;
		opacitySpinner.ClimbRate = 1;
		opacitySpinner.OnValueChanged += OnOpacitySpinnerChanged;
		opacitySpinner.SetActivatesDefaultImmediate (true);

		Gtk.Scale opacitySlider = Gtk.Scale.NewWithRange (Gtk.Orientation.Horizontal, 0, 100, 1);
		opacitySlider.Digits = 0;
		opacitySlider.Adjustment!.PageIncrement = 10;
		opacitySlider.Hexpand = true;
		opacitySlider.Halign = Gtk.Align.Fill;
		opacitySlider.OnValueChanged += OnOpacitySliderChanged;

		Gtk.Box opacityBox = Gtk.Box.New (Gtk.Orientation.Horizontal, spacing);
		opacityBox.Append (opacitySpinner);
		opacityBox.Append (opacitySlider);

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = spacing;
		grid.ColumnSpacing = spacing;
		grid.ColumnHomogeneous = false;
		grid.Attach (nameLabel, 0, 0, 1, 1);
		grid.Attach (layerNameEntry, 1, 0, 1, 1);
		grid.Attach (visibilityCheckbox, 1, 1, 1, 1);
		grid.Attach (blendLabel, 0, 2, 1, 1);
		grid.Attach (blendComboBox, 1, 2, 1, 1);
		grid.Attach (opacityLabel, 0, 3, 1, 1);
		grid.Attach (opacityBox, 1, 3, 1, 1);

		// --- Initialization (Gtk.Window)

		Title = Translations.GetString ("Layer Properties");
		DefaultWidth = 349;
		DefaultHeight = 224;
		IconName = Resources.Icons.LayerProperties;

		// --- Initialization (Gtk.Dialog)

		this.AddCancelOkButtons ();
		this.SetDefaultResponse (Gtk.ResponseType.Ok);

		// --- Initialization

		var contentArea = this.GetContentAreaBox ();
		contentArea.Spacing = spacing;
		contentArea.SetAllMargins (10);
		contentArea.Append (grid);

		// --- References to keep

		layer_name_entry = layerNameEntry;
		visibility_checkbox = visibilityCheckbox;
		blend_combo_box = blendComboBox;
		opacity_spinner = opacitySpinner;
		opacity_slider = opacitySlider;
	}

	public static LayerPropertiesDialog New (IChromeService chrome, Document document, UserLayer layer)
	{
		LayerPropertiesDialog dialog = NewWithProperties ([]);
		dialog.Configure (chrome, document, layer);
		return dialog;
	}

	private void Configure (IChromeService chrome, Document document, UserLayer layer)
	{
		this.document = document;
		this.layer = layer;
		TransientFor = chrome.MainWindow;

		string currentLayerName = layer.Name;
		bool currentLayerHidden = layer.Hidden;
		double currentLayerOpacity = layer.Opacity;
		BlendMode currentLayerBlendMode = layer.BlendMode;

		LayerProperties initialProperties = new (
			currentLayerName,
			currentLayerHidden,
			currentLayerOpacity,
			currentLayerBlendMode);

		layer_name_entry.SetText (initialProperties.Name);
		visibility_checkbox.Active = !initialProperties.Hidden;
		opacity_spinner.Value = Math.Round (initialProperties.Opacity * 100);
		opacity_slider.SetValue (Math.Round (initialProperties.Opacity * 100));

		var allBlendmodes = UserBlendOps.GetAllBlendModeNames ().ToImmutableArray ();
		var index = allBlendmodes.IndexOf (UserBlendOps.GetBlendModeName (currentLayerBlendMode));
		blend_combo_box.Active = index;

		current_layer_name = currentLayerName;
		current_layer_hidden = currentLayerHidden;
		current_layer_opacity = currentLayerOpacity;
		current_layer_blend_mode = currentLayerBlendMode;

		initial_properties = initialProperties;
	}

	public bool AreLayerPropertiesUpdated =>
		initial_properties.Opacity != current_layer_opacity
		|| initial_properties.Hidden != current_layer_hidden
		|| initial_properties.Name != current_layer_name
		|| initial_properties.BlendMode != current_layer_blend_mode;

	public LayerProperties InitialLayerProperties
		=> initial_properties;

	public LayerProperties UpdatedLayerProperties
		=> new (
			current_layer_name,
			current_layer_hidden,
			current_layer_opacity,
			current_layer_blend_mode);

	private void OnLayerNameChanged (object? sender, EventArgs e)
	{
		current_layer_name = layer_name_entry.GetText ();
		layer.Name = current_layer_name;
	}

	private void OnVisibilityToggled (object? sender, EventArgs e)
	{
		current_layer_hidden = !visibility_checkbox.Active;
		layer.Hidden = current_layer_hidden;

		if (document.Layers.CurrentUserLayer == layer)
			document.Layers.SelectionLayer.Hidden = layer.Hidden;

		document.Workspace.Invalidate ();
	}

	private void OnOpacitySliderChanged (object? sender, EventArgs e)
	{
		opacity_spinner.Value = opacity_slider.GetValue ();
		UpdateOpacity ();
	}

	private void OnOpacitySpinnerChanged (object? sender, EventArgs e)
	{
		opacity_slider.SetValue (opacity_spinner.Value);
		UpdateOpacity ();
	}

	private void UpdateOpacity ()
	{
		//TODO check redraws are being throttled.
		current_layer_opacity = opacity_spinner.Value / 100d;
		layer.Opacity = current_layer_opacity;

		if (document.Layers.CurrentUserLayer == layer)
			document.Layers.SelectionLayer.Opacity = layer.Opacity;

		document.Workspace.Invalidate ();
	}

	private void OnBlendModeChanged (object? sender, EventArgs e)
	{
		current_layer_blend_mode = UserBlendOps.GetBlendModeByName (blend_combo_box.GetActiveText ()!);
		layer.BlendMode = current_layer_blend_mode;

		if (document.Layers.CurrentUserLayer == layer)
			document.Layers.SelectionLayer.BlendMode = layer.BlendMode;

		document.Workspace.Invalidate ();
	}
}

