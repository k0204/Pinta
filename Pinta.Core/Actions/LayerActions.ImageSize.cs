//
// LayerActions.ImageSize.cs
//

using System;
using System.Collections.Generic;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private sealed class AiImageSizePicker
	{
		private readonly Gtk.ComboBoxText presetCombobox = Gtk.ComboBoxText.New ();
		private readonly Gtk.Grid customGrid = Gtk.Grid.New ();
		private readonly Gtk.SpinButton widthSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 16);
		private readonly Gtk.SpinButton heightSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 16);
		private readonly Gtk.Label validationLabel = Gtk.Label.New (string.Empty);
		private readonly List<Size> presets = [];
		private bool gptSelected;
		private bool updating;

		public AiImageSizePicker ()
		{
			widthSpinner.Value = 1024;
			heightSpinner.Value = 1024;
			widthSpinner.Hexpand = true;
			heightSpinner.Hexpand = true;

			customGrid.ColumnSpacing = 8;
			customGrid.RowSpacing = 4;
			customGrid.Attach (Gtk.Label.New (Translations.GetString ("Width:")), 0, 0, 1, 1);
			customGrid.Attach (widthSpinner, 1, 0, 1, 1);
			customGrid.Attach (Gtk.Label.New (Translations.GetString ("Height:")), 0, 1, 1, 1);
			customGrid.Attach (heightSpinner, 1, 1, 1, 1);

			validationLabel.Halign = Gtk.Align.Start;
			validationLabel.Wrap = true;
			validationLabel.AddCssClass (AdwaitaStyles.Error);

			Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
			content.Append (presetCombobox);
			content.Append (customGrid);
			content.Append (validationLabel);
			Widget = content;

			presetCombobox.OnChanged += (_, _) => HandleChanged ();
			widthSpinner.OnValueChanged += (_, _) => HandleChanged ();
			heightSpinner.OnValueChanged += (_, _) => HandleChanged ();
		}

		public Gtk.Widget Widget { get; }
		public event EventHandler? Changed;

		public Size? SelectedSize {
			get {
				if (gptSelected && presetCombobox.Active == presets.Count) {
					Size custom = new ((int) widthSpinner.Value, (int) heightSpinner.Value);
					return AI.BackgroundCutoutService.GetGptImageSizeError (custom) is null ? custom : null;
				}
				return presetCombobox.Active >= 0 && presetCombobox.Active < presets.Count
					? presets[presetCombobox.Active]
					: null;
			}
		}

		public bool IsValid => SelectedSize is not null;

		public void SetService (string imageService)
		{
			updating = true;
			gptSelected = imageService == AI.AiRequestSettings.GptImageService;
			presetCombobox.RemoveAll ();
			presets.Clear ();
			presets.AddRange (AI.BackgroundCutoutService.GetImageGenerationSizes (imageService));
			foreach (Size size in presets)
				presetCombobox.AppendText ($"{size.Width} x {size.Height}");
			if (gptSelected)
				presetCombobox.AppendText (Translations.GetString ("Custom..."));
			presetCombobox.Active = presets.FindIndex (size => size == new Size (1024, 1024));
			if (presetCombobox.Active < 0)
				presetCombobox.Active = 0;
			updating = false;
			Refresh ();
			Changed?.Invoke (this, EventArgs.Empty);
		}

		private void HandleChanged ()
		{
			if (updating)
				return;
			Refresh ();
			Changed?.Invoke (this, EventArgs.Empty);
		}

		private void Refresh ()
		{
			bool custom = gptSelected && presetCombobox.Active == presets.Count;
			customGrid.Visible = custom;
			string? error = custom
				? AI.BackgroundCutoutService.GetGptImageSizeError (new ((int) widthSpinner.Value, (int) heightSpinner.Value))
				: null;
			validationLabel.SetText (error ?? string.Empty);
			validationLabel.Visible = error is not null;
		}
	}
}
