//
// ResizeLayerDialog.cs
//
// Author:
//       Pinta contributors
//
// Copyright (c) 2026 Pinta contributors
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
using System.Diagnostics.CodeAnalysis;
using Pinta.Core;

namespace Pinta;

[GObject.Subclass<Gtk.Dialog>]
public sealed partial class ResizeLayerDialog
{
	private Gtk.SpinButton percentage_spinner;
	private Gtk.SpinButton width_spinner;
	private Gtk.SpinButton height_spinner;
	private Gtk.CheckButton aspect_checkbox;
	private Gtk.CheckButton percentage_radio;
	private Gtk.CheckButton absolute_radio;
	private Gtk.ComboBoxText resampling_combobox;

	private ISettingsService settings = null!;
	private Size layer_size;
	private bool value_changing;

	const int SPACING = 6;

	[MemberNotNull (nameof (percentage_spinner), nameof (width_spinner), nameof (height_spinner))]
	[MemberNotNull (nameof (aspect_checkbox), nameof (absolute_radio), nameof (percentage_radio), nameof (resampling_combobox))]
	partial void Initialize ()
	{
		BoxStyle spacedHorizontal = new (
			orientation: Gtk.Orientation.Horizontal,
			spacing: SPACING);

		BoxStyle spacedVertical = new (
			orientation: Gtk.Orientation.Vertical,
			spacing: SPACING);

		Gtk.SpinButton percentageSpinner = Gtk.SpinButton.NewWithRange (1, int.MaxValue, 1);
		percentageSpinner.OnValueChanged += percentageSpinner_ValueChanged;
		percentageSpinner.SetActivatesDefaultImmediate (true);

		Gtk.SpinButton widthSpinner = Gtk.SpinButton.NewWithRange (1, int.MaxValue, 1);
		widthSpinner.OnValueChanged += widthSpinner_ValueChanged;
		widthSpinner.SetActivatesDefaultImmediate (true);

		Gtk.SpinButton heightSpinner = Gtk.SpinButton.NewWithRange (1, int.MaxValue, 1);
		heightSpinner.OnValueChanged += heightSpinner_ValueChanged;
		heightSpinner.SetActivatesDefaultImmediate (true);

		Gtk.CheckButton aspectCheckbox = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Maintain aspect ratio"));

		Gtk.Button resetButton = Gtk.Button.NewFromIconName (Resources.StandardIcons.EditUndo);
		resetButton.WidthRequest = 24;
		resetButton.HeightRequest = 24;
		resetButton.TooltipText = Translations.GetString ("Reset to layer size");
		resetButton.OnClicked += OnResetButtonClicked;

		Gtk.CheckButton percentageRadio = Gtk.CheckButton.NewWithLabel (Translations.GetString ("By percentage:"));
		percentageRadio.BindProperty (
			Gtk.CheckButton.ActivePropertyDefinition.UnmanagedName,
			percentageSpinner,
			Gtk.SpinButton.SensitivePropertyDefinition.UnmanagedName,
			GObject.BindingFlags.SyncCreate);

		Gtk.CheckButton absoluteRadio = Gtk.CheckButton.NewWithLabel (Translations.GetString ("By absolute size:"));
		absoluteRadio.SetGroup (percentageRadio);
		absoluteRadio.BindProperty (
			Gtk.CheckButton.ActivePropertyDefinition.UnmanagedName,
			widthSpinner,
			Gtk.SpinButton.SensitivePropertyDefinition.UnmanagedName,
			GObject.BindingFlags.SyncCreate);
		absoluteRadio.BindProperty (
			Gtk.CheckButton.ActivePropertyDefinition.UnmanagedName,
			heightSpinner,
			Gtk.SpinButton.SensitivePropertyDefinition.UnmanagedName,
			GObject.BindingFlags.SyncCreate);
		absoluteRadio.BindProperty (
			Gtk.CheckButton.ActivePropertyDefinition.UnmanagedName,
			aspectCheckbox,
			Gtk.CheckButton.SensitivePropertyDefinition.UnmanagedName,
			GObject.BindingFlags.SyncCreate);
		absoluteRadio.BindProperty (
			Gtk.CheckButton.ActivePropertyDefinition.UnmanagedName,
			resetButton,
			Gtk.Button.SensitivePropertyDefinition.UnmanagedName,
			GObject.BindingFlags.SyncCreate);

		Gtk.ComboBoxText resamplingCombobox = CreateResamplingCombobox ();

		Gtk.Box hboxPercent = GtkExtensions.Box (
			spacedHorizontal,
			[
				percentageRadio,
				percentageSpinner,
				Gtk.Label.New ("%"),
			]);

		Gtk.Label widthLabel = Gtk.Label.New (Translations.GetString ("Width:"));
		widthLabel.Halign = Gtk.Align.End;

		Gtk.Label heightLabel = Gtk.Label.New (Translations.GetString ("Height:"));
		heightLabel.Halign = Gtk.Align.End;

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = SPACING;
		grid.ColumnSpacing = SPACING;
		grid.ColumnHomogeneous = false;
		grid.Attach (widthLabel, 0, 0, 1, 1);
		grid.Attach (widthSpinner, 1, 0, 1, 1);
		grid.Attach (Gtk.Label.New (Translations.GetString ("pixels")), 2, 0, 1, 1);
		grid.Attach (resetButton, 3, 0, 1, 1);
		grid.Attach (heightLabel, 0, 1, 1, 1);
		grid.Attach (heightSpinner, 1, 1, 1, 1);
		grid.Attach (Gtk.Label.New (Translations.GetString ("pixels")), 2, 1, 1, 1);
		grid.Attach (aspectCheckbox, 0, 2, 3, 1);
		grid.Attach (Gtk.Label.New (Translations.GetString ("Resampling:")), 0, 3, 1, 1);
		grid.Attach (resamplingCombobox, 1, 3, 2, 1);

		Gtk.Box mainVbox = GtkExtensions.Box (
			spacedVertical,
			[
				hboxPercent,
				absoluteRadio,
				grid,
			]);

		Title = Translations.GetString ("Resize Layer");
		Modal = true;
		IconName = Resources.Icons.ImageResize;
		DefaultWidth = 300;
		DefaultHeight = 200;

		this.AddCancelOkButtons ();
		this.SetDefaultResponse (Gtk.ResponseType.Ok);
		OnResponse += OnDialogResponse;

		Gtk.Box contentArea = this.GetContentAreaBox ();
		contentArea.SetAllMargins (12);
		contentArea.Append (mainVbox);

		percentage_spinner = percentageSpinner;
		width_spinner = widthSpinner;
		height_spinner = heightSpinner;
		aspect_checkbox = aspectCheckbox;
		absolute_radio = absoluteRadio;
		percentage_radio = percentageRadio;
		resampling_combobox = resamplingCombobox;
	}

	private void Configure (IChromeService chrome, ISettingsService settings, Size layerSize)
	{
		TransientFor = chrome.MainWindow;
		this.settings = settings;
		layer_size = layerSize;

		value_changing = true;
		width_spinner.Value = layer_size.Width;
		height_spinner.Value = layer_size.Height;
		percentage_spinner.Value = settings.GetSetting (SettingNames.RESIZE_LAYER_PERCENTAGE, 100);
		value_changing = false;

		aspect_checkbox.Active = settings.GetSetting (SettingNames.RESIZE_LAYER_MAINTAIN_ASPECT, true);
		resampling_combobox.Active = settings.GetSetting (SettingNames.RESIZE_LAYER_RESAMPLING, 0);

		if (settings.GetSetting (SettingNames.RESIZE_LAYER_USE_PERCENTAGE, true))
			percentage_radio.Active = true;
		else
			absolute_radio.Active = true;

		percentageSpinner_ValueChanged (null, EventArgs.Empty);
		percentage_spinner.GrabFocus ();
	}

	internal static ResizeLayerDialog New (IChromeService chrome, ISettingsService settings, Size layerSize)
	{
		ResizeLayerDialog dialog = NewWithProperties ([]);
		dialog.Configure (chrome, settings, layerSize);
		return dialog;
	}

	private void OnDialogResponse (Gtk.Dialog sender, ResponseSignalArgs args)
	{
		if (args.ResponseId != (int) Gtk.ResponseType.Ok)
			return;

		settings.PutSetting (SettingNames.RESIZE_LAYER_MAINTAIN_ASPECT, aspect_checkbox.Active);
		settings.PutSetting (SettingNames.RESIZE_LAYER_USE_PERCENTAGE, percentage_radio.Active);
		settings.PutSetting (SettingNames.RESIZE_LAYER_PERCENTAGE, percentage_spinner.GetValueAsInt ());
		settings.PutSetting (SettingNames.RESIZE_LAYER_RESAMPLING, resampling_combobox.Active);
	}

	private static Gtk.ComboBoxText CreateResamplingCombobox ()
	{
		Gtk.ComboBoxText result = Gtk.ComboBoxText.New ();
		result.Hexpand = true;
		result.Halign = Gtk.Align.Fill;

		foreach (ResamplingMode mode in Enum.GetValues (typeof (ResamplingMode)))
			result.AppendText (mode.GetLabel ());

		result.Active = 0;

		return result;
	}

	public ResizeLayerOptions GetResizeLayerOptions ()
	{
		Size newSize = new (
			Width: width_spinner.GetValueAsInt (),
			Height: height_spinner.GetValueAsInt ());
		ResamplingMode resamplingMode = (ResamplingMode) resampling_combobox.Active;
		return new (newSize, resamplingMode);
	}

	private void heightSpinner_ValueChanged (object? sender, EventArgs e)
	{
		if (value_changing || !aspect_checkbox.Active)
			return;

		value_changing = true;
		width_spinner.Value = Math.Max (1, (int) (height_spinner.Value * layer_size.Width / layer_size.Height));
		value_changing = false;
	}

	private void widthSpinner_ValueChanged (object? sender, EventArgs e)
	{
		if (value_changing || !aspect_checkbox.Active)
			return;

		value_changing = true;
		height_spinner.Value = Math.Max (1, (int) (width_spinner.Value * layer_size.Height / layer_size.Width));
		value_changing = false;
	}

	private void percentageSpinner_ValueChanged (object? sender, EventArgs e)
	{
		if (value_changing)
			return;

		float proportion = percentage_spinner.GetValueAsInt () / 100f;
		value_changing = true;
		width_spinner.Value = Math.Max (1, (int) (layer_size.Width * proportion));
		height_spinner.Value = Math.Max (1, (int) (layer_size.Height * proportion));
		value_changing = false;
	}

	void OnResetButtonClicked (Gtk.Button button, EventArgs eventArgs)
	{
		value_changing = true;
		width_spinner.Value = layer_size.Width;
		height_spinner.Value = layer_size.Height;
		value_changing = false;
	}
}
