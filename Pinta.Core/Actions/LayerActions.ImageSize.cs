//
// LayerActions.ImageSize.cs
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private sealed class AiImageSizePicker
	{
		private readonly Gtk.ComboBoxText presetCombobox = Gtk.ComboBoxText.New ();
		private readonly Gtk.ComboBoxText resolutionCombobox = Gtk.ComboBoxText.New ();
		private readonly Gtk.ComboBoxText aspectRatioCombobox = Gtk.ComboBoxText.New ();
		private readonly Gtk.Grid customGrid = Gtk.Grid.New ();
		private readonly Gtk.Label customResolutionLabel = Gtk.Label.New (string.Empty);
		private readonly Gtk.SpinButton widthSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 16);
		private readonly Gtk.SpinButton heightSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 16);
		private readonly Gtk.Label validationLabel = Gtk.Label.New (string.Empty);
		private readonly List<Size> presets = [];
		private readonly List<string> gptResolutionTiers = [];
		private readonly List<AI.NanoBananaImageOption> nanoBananaOptions = [];
		private bool gptSelected;
		private bool nanoBananaSelected;
		private bool updating;
		private string? configuredService;
		private string? configuredProvider;

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
			customResolutionLabel.Halign = Gtk.Align.Start;
			customResolutionLabel.AddCssClass (AdwaitaStyles.DimLabel);
			customGrid.Attach (customResolutionLabel, 1, 2, 1, 1);

			Gtk.Grid nanoBananaGrid = Gtk.Grid.New ();
			nanoBananaGrid.ColumnSpacing = 8;
			nanoBananaGrid.RowSpacing = 4;
			nanoBananaGrid.Attach (Gtk.Label.New (Translations.GetString ("Resolution:")), 0, 0, 1, 1);
			nanoBananaGrid.Attach (resolutionCombobox, 1, 0, 1, 1);
			nanoBananaGrid.Attach (Gtk.Label.New (Translations.GetString ("Aspect ratio:")), 0, 1, 1, 1);
			nanoBananaGrid.Attach (aspectRatioCombobox, 1, 1, 1, 1);

			validationLabel.Halign = Gtk.Align.Start;
			validationLabel.Wrap = true;
			validationLabel.AddCssClass (AdwaitaStyles.Error);

			Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
			content.Append (presetCombobox);
			content.Append (customGrid);
			content.Append (nanoBananaGrid);
			content.Append (validationLabel);
			Widget = content;

			presetCombobox.OnChanged += (_, _) => HandleChanged ();
			resolutionCombobox.OnChanged += (_, _) => HandleResolutionChanged ();
			aspectRatioCombobox.OnChanged += (_, _) => HandleChanged ();
			widthSpinner.OnValueChanged += (_, _) => HandleChanged ();
			heightSpinner.OnValueChanged += (_, _) => HandleChanged ();
		}

		public Gtk.Widget Widget { get; }
		public event EventHandler? Changed;

		public Size? SelectedSize {
			get {
				if (nanoBananaSelected)
					return GetSelectedNanoBananaSize ();
				if (gptSelected)
					return GetSelectedGptSize ();
				return presetCombobox.Active >= 0 && presetCombobox.Active < presets.Count
					? presets[presetCombobox.Active]
					: null;
			}
		}

		public bool IsValid => SelectedSize is not null;

		public void SetService (string imageService, string? provider = null)
		{
			bool preserveSelection = configuredService == imageService && configuredProvider == provider;
			Size? previousSize = preserveSelection ? SelectedSize : null;
			updating = true;
			gptSelected = imageService == AI.AiRequestSettings.GptImageService;
			nanoBananaSelected = imageService == AI.AiRequestSettings.NanoBananaService;
			presetCombobox.RemoveAll ();
			presets.Clear ();
			gptResolutionTiers.Clear ();
			nanoBananaOptions.Clear ();
			if (nanoBananaSelected)
				nanoBananaOptions.AddRange (AI.NanoBananaImageConfig.GetImageGenerationOptions ());
			else
				presets.AddRange (AI.BackgroundCutoutService.GetImageGenerationSizes (imageService, provider));
			presetCombobox.Visible = !gptSelected && !nanoBananaSelected;
			customGrid.Visible = false;
			resolutionCombobox.Visible = gptSelected || nanoBananaSelected;
			aspectRatioCombobox.Visible = gptSelected || nanoBananaSelected;
			if (nanoBananaSelected)
				SetNanoBananaOptions ();
			else if (gptSelected)
				SetGptOptions ();
			else {
				foreach (Size size in presets)
					presetCombobox.AppendText ($"{size.Width} x {size.Height}");
				presetCombobox.Active = presets.FindIndex (size => size == new Size (1024, 1024));
				if (presetCombobox.Active < 0)
					presetCombobox.Active = 0;
			}
			if (previousSize is Size selectedSize)
				RestoreSelection (selectedSize);
			updating = false;
			configuredService = imageService;
			configuredProvider = provider;
			Refresh ();
			Changed?.Invoke (this, EventArgs.Empty);
		}

		private void RestoreSelection (Size size)
		{
			if (nanoBananaSelected) {
				List<string> resolutions = [.. nanoBananaOptions.Select (option => option.Resolution).Distinct ()];
				int resolutionIndex = resolutions.FindIndex (resolution => nanoBananaOptions.Any (
					option => option.Resolution == resolution && option.Size == size));
				if (resolutionIndex < 0)
					return;
				resolutionCombobox.Active = resolutionIndex;
				UpdateNanoBananaAspectRatios ();
				AI.NanoBananaImageOption[] options = [.. nanoBananaOptions.Where (
					option => option.Resolution == resolutions[resolutionIndex])];
				aspectRatioCombobox.Active = Array.FindIndex (options, option => option.Size == size);
				return;
			}

			if (gptSelected) {
				string tier = GetGptResolutionTier (size);
				int tierIndex = gptResolutionTiers.IndexOf (tier);
				if (tierIndex < 0)
					return;
				resolutionCombobox.Active = tierIndex;
				UpdateGptAspectRatios ();
				Size[] options = [.. presets.Where (preset => GetGptResolutionTier (preset) == tier)];
				aspectRatioCombobox.Active = Array.IndexOf (options, size);
				return;
			}

			presetCombobox.Active = presets.IndexOf (size);
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
			bool custom = gptSelected && resolutionCombobox.Active == gptResolutionTiers.Count;
			customGrid.Visible = custom && !nanoBananaSelected;
			string? error = custom
				? AI.BackgroundCutoutService.GetGptImageSizeError (new ((int) widthSpinner.Value, (int) heightSpinner.Value))
				: null;
			if (custom)
				customResolutionLabel.SetText (Translations.GetString (
					"Detected resolution: {0}",
					GetResolutionLabel (GetGptResolutionTier (new ((int) widthSpinner.Value, (int) heightSpinner.Value)))));
			validationLabel.SetText (error ?? string.Empty);
			validationLabel.Visible = error is not null;
		}

		private void SetNanoBananaOptions ()
		{
			resolutionCombobox.RemoveAll ();
			foreach (string resolution in nanoBananaOptions.Select (option => option.Resolution).Distinct ())
				resolutionCombobox.AppendText (GetResolutionLabel (resolution));
			resolutionCombobox.Active = 0;
			UpdateNanoBananaAspectRatios ();
		}

		private static string GetResolutionLabel (string resolution)
			=> resolution switch {
				"1K" => Translations.GetString ("1K"),
				"2K" => Translations.GetString ("2K"),
				"4K" => Translations.GetString ("4K"),
				_ => resolution,
			};

		private void HandleResolutionChanged ()
		{
			if (updating)
				return;
			if (nanoBananaSelected)
				UpdateNanoBananaAspectRatios ();
			else if (gptSelected)
				UpdateGptAspectRatios ();
			HandleChanged ();
		}

		private void SetGptOptions ()
		{
			foreach (string tier in new[] { "1K", "2K", "4K" })
				if (presets.Any (size => GetGptResolutionTier (size) == tier))
					gptResolutionTiers.Add (tier);
			resolutionCombobox.RemoveAll ();
			foreach (string tier in gptResolutionTiers)
				resolutionCombobox.AppendText (GetResolutionLabel (tier));
			resolutionCombobox.AppendText (Translations.GetString ("Custom..."));
			resolutionCombobox.Active = gptResolutionTiers.Count > 0 ? 0 : -1;
			UpdateGptAspectRatios ();
		}

		private void UpdateGptAspectRatios ()
		{
			string? tier = resolutionCombobox.Active < gptResolutionTiers.Count
				? gptResolutionTiers[resolutionCombobox.Active]
				: null;
			aspectRatioCombobox.RemoveAll ();
			foreach (Size size in presets.Where (size => GetGptResolutionTier (size) == tier))
				aspectRatioCombobox.AppendText ($"{GetAspectRatio (size)} ({size.Width} x {size.Height})");
			aspectRatioCombobox.Visible = tier is not null;
			aspectRatioCombobox.Active = tier is not null ? 0 : -1;
		}

		private Size? GetSelectedGptSize ()
		{
			if (resolutionCombobox.Active == gptResolutionTiers.Count) {
				Size custom = new ((int) widthSpinner.Value, (int) heightSpinner.Value);
				return AI.BackgroundCutoutService.GetGptImageSizeError (custom) is null ? custom : null;
			}

			string? tier = resolutionCombobox.Active >= 0 && resolutionCombobox.Active < gptResolutionTiers.Count
				? gptResolutionTiers[resolutionCombobox.Active]
				: null;
			Size[] options = [.. presets.Where (size => GetGptResolutionTier (size) == tier)];
			return aspectRatioCombobox.Active >= 0 && aspectRatioCombobox.Active < options.Length
				? options[aspectRatioCombobox.Active]
				: null;
		}

		private static string GetGptResolutionTier (Size size)
		{
			const long oneKPixelLimit = 1024L * 1024;
			const long twoKPixelLimit = 2048L * 2048;
			long pixels = (long) size.Width * size.Height;
			return pixels <= oneKPixelLimit ? "1K" : pixels <= twoKPixelLimit ? "2K" : "4K";
		}

		private static string GetAspectRatio (Size size)
		{
			int divisor = GreatestCommonDivisor (size.Width, size.Height);
			return $"{size.Width / divisor}:{size.Height / divisor}";
		}

		private static int GreatestCommonDivisor (int left, int right)
		{
			while (right != 0)
				(left, right) = (right, left % right);
			return Math.Abs (left);
		}

		private void UpdateNanoBananaAspectRatios ()
		{
			string? resolution = resolutionCombobox.GetActiveText ();
			aspectRatioCombobox.RemoveAll ();
			foreach (AI.NanoBananaImageOption option in nanoBananaOptions.Where (option => option.Resolution == resolution))
				aspectRatioCombobox.AppendText ($"{option.AspectRatio} ({option.Size.Width} x {option.Size.Height})");
			aspectRatioCombobox.Active = 0;
		}

		private Size? GetSelectedNanoBananaSize ()
		{
			string? resolution = resolutionCombobox.GetActiveText ();
			int optionIndex = aspectRatioCombobox.Active;
			AI.NanoBananaImageOption[] options = [.. nanoBananaOptions.Where (option => option.Resolution == resolution)];
			return optionIndex >= 0 && optionIndex < options.Length ? options[optionIndex].Size : null;
		}
	}
}
