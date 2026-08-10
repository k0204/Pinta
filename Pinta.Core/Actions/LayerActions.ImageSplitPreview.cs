using System;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private static ImageSplitPreviewControls CreateImageSplitPreviewControls (UserLayer source)
		=> new (source);

	private sealed class ImageSplitPreviewControls : IDisposable
	{
		private readonly Size sourceSize;
		private readonly Cairo.ImageSurface sourceSurface;
		private readonly Gdk.Texture sourceTexture;
		private Cairo.ImageSurface? adaptedSurface;
		private Gdk.Texture? adaptedTexture;
		private readonly Gtk.Box content;
		private readonly Gtk.Box resolutionChoices;
		private readonly Gtk.CheckButton lowerButton;
		private readonly Gtk.CheckButton upperButton;
		private readonly Gtk.CheckButton customButton;
		private readonly Gtk.Grid customGrid;
		private readonly Gtk.SpinButton widthSpinner;
		private readonly Gtk.SpinButton heightSpinner;
		private readonly Gtk.Label validationLabel;
		private readonly Gtk.CheckButton paddingButton;
		private readonly Gtk.Picture adaptedPicture;
		private readonly Gtk.DrawingArea adaptedBackdrop;
		private readonly Gtk.DrawingArea adaptedBorder;
		private readonly Gtk.Label resolutionTitle;
		private readonly Gtk.Grid comparison;
		private readonly Gtk.Overlay singlePreview;
		private readonly Gtk.Grid infoGrid;
		private readonly Gtk.Label originalSizeLabel;
		private readonly Gtk.Label requestSizeLabel;
		private readonly Gtk.Label scaleLabel;
		private readonly Gtk.Label paddingLabel;
		private Size? lowerSize;
		private Size? upperSize;
		private string imageService = AI.AiRequestSettings.GptImageService;
		private string choice = "lower";
		private bool updating;

		public ImageSplitPreviewControls (UserLayer source)
		{
			sourceSurface = RenderLayerContent (source, out _);
			sourceSize = sourceSurface.GetSize ();

			lowerButton = Gtk.CheckButton.New ();
			upperButton = Gtk.CheckButton.New ();
			customButton = Gtk.CheckButton.New ();
			resolutionChoices = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
			foreach (Gtk.CheckButton button in new[] { lowerButton, upperButton, customButton }) {
				button.Hexpand = true;
			}
			resolutionChoices.AddCssClass (AdwaitaStyles.Linked);
			resolutionChoices.Append (lowerButton);
			resolutionChoices.Append (upperButton);
			resolutionChoices.Append (customButton);
			upperButton.SetGroup (lowerButton);
			customButton.SetGroup (upperButton);

			widthSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 1);
			heightSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 1);
			widthSpinner.Value = Math.Clamp (sourceSize.Width, 16, 3840);
			heightSpinner.Value = Math.Clamp (sourceSize.Height, 16, 3840);
			customGrid = Gtk.Grid.New ();
			customGrid.ColumnSpacing = 8;
			customGrid.Attach (Gtk.Label.New (Translations.GetString ("Width:")), 0, 0, 1, 1);
			customGrid.Attach (widthSpinner, 1, 0, 1, 1);
			customGrid.Attach (Gtk.Label.New (Translations.GetString ("Height:")), 2, 0, 1, 1);
			customGrid.Attach (heightSpinner, 3, 0, 1, 1);

			validationLabel = Gtk.Label.New (string.Empty);
			validationLabel.Halign = Gtk.Align.Start;
			validationLabel.Wrap = true;
			validationLabel.AddCssClass (AdwaitaStyles.Error);

			paddingButton = Gtk.CheckButton.NewWithLabel (Translations.GetString ("White padding"));
			paddingButton.Active = true;
			originalSizeLabel = Gtk.Label.New (FormatSize (sourceSize));
			requestSizeLabel = Gtk.Label.New (string.Empty);
			scaleLabel = Gtk.Label.New (string.Empty);
			paddingLabel = Gtk.Label.New (string.Empty);
			foreach (Gtk.Label label in new[] { originalSizeLabel, requestSizeLabel, scaleLabel, paddingLabel })
				label.Halign = Gtk.Align.Start;

			infoGrid = Gtk.Grid.New ();
			infoGrid.RowSpacing = 4;
			infoGrid.ColumnSpacing = 12;
			AttachSettingsRow (infoGrid, Translations.GetString ("Original size:"), originalSizeLabel, 0);
			AttachSettingsRow (infoGrid, Translations.GetString ("AI request size:"), requestSizeLabel, 1);
			AttachSettingsRow (infoGrid, Translations.GetString ("Scale:"), scaleLabel, 2);
			AttachSettingsRow (infoGrid, Translations.GetString ("Padding:"), paddingLabel, 3);

			sourceTexture = sourceSurface.ToTexture ();
			Gtk.Overlay originalPreview = CreatePreviewWidget (
				sourceTexture,
				checkerboard: true,
				out _,
				out _,
				out Gtk.DrawingArea originalBorder);
			originalBorder.SetDrawFunc ((_, context, width, height)
				=> DrawPreviewBorder (context, width, height, sourceSize));
			Gtk.Overlay adaptedPreview = CreatePreviewWidget (
				sourceTexture,
				checkerboard: false,
				out adaptedPicture,
				out adaptedBackdrop,
				out adaptedBorder);
			adaptedBackdrop.SetDrawFunc ((_, context, width, height)
				=> DrawPreviewBackground (
					context,
					width,
					height,
					!paddingButton.Active));
			adaptedBorder.SetDrawFunc ((_, context, width, height)
				=> DrawPreviewBorder (context, width, height, GetSelectedSize ()));
			comparison = Gtk.Grid.New ();
			comparison.ColumnSpacing = 12;
			comparison.RowSpacing = 6;
			comparison.Hexpand = true;
			comparison.Attach (CreatePreviewHeading (Translations.GetString ("Original image")), 0, 0, 1, 1);
			comparison.Attach (CreatePreviewHeading (Translations.GetString ("Adapted image")), 1, 0, 1, 1);
			comparison.Attach (originalPreview, 0, 1, 1, 1);
			comparison.Attach (adaptedPreview, 1, 1, 1, 1);
			singlePreview = CreatePreviewWidget (
				sourceTexture,
				checkerboard: true,
				out _,
				out _,
				out Gtk.DrawingArea singleBorder);
			singleBorder.SetDrawFunc ((_, context, width, height)
				=> DrawPreviewBorder (context, width, height, sourceSize));

			content = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
			content.Append (CreateDialogLabel (Translations.GetString ("Image Split Preview")));
			resolutionTitle = CreateDialogLabel (Translations.GetString ("Resolution:"));
			content.Append (resolutionTitle);
			content.Append (resolutionChoices);
			content.Append (customGrid);
			content.Append (validationLabel);
			content.Append (comparison);
			content.Append (singlePreview);
			content.Append (infoGrid);
			content.Append (paddingButton);

			lowerButton.OnToggled += (_, _) => SelectResolution ("lower");
			upperButton.OnToggled += (_, _) => SelectResolution ("upper");
			customButton.OnToggled += (_, _) => SelectResolution ("custom");
			widthSpinner.OnValueChanged += (_, _) => UpdatePreview ();
			heightSpinner.OnValueChanged += (_, _) => UpdatePreview ();
			paddingButton.OnToggled += (_, _) => {
				paddingButton.Label = Translations.GetString (
					paddingButton.Active ? "White padding" : "Transparent padding");
				UpdatePreview ();
			};

			SetService (imageService, AI.AiRequestSettings.GetGptProvider (PintaCore.Settings));
		}

		public Gtk.Widget Widget => content;

		public Size SourceSize => sourceSize;

		public bool IsValid
		{
			get {
				Size? selected = GetSelectedSize ();
				return selected is Size && GetValidationError (selected) is null;
			}
		}

		public ImageSplitPreviewSelection? Selection
		{
			get {
				TryGetSelection (out ImageSplitPreviewSelection? selection);
				return selection;
			}
		}

		public void SetService (string imageService, string provider)
		{
			this.imageService = imageService;
			AI.AiImageResolutionPlan plan = AI.AiImageResolutionPlanner.Create (
				imageService,
				provider,
				sourceSize);
			lowerSize = plan.LowerSize;
			upperSize = plan.UpperSize;
			choice = lowerSize is not null ? "lower" : upperSize is not null ? "upper" : string.Empty;
			UpdateResolutionChoices ();
			UpdatePreview ();
		}

		public void Dispose ()
		{
			ClearAdaptedPreview ();
			sourceTexture.Dispose ();
			sourceSurface.Dispose ();
		}

		private void UpdateResolutionChoices ()
		{
			lowerButton.Label = lowerSize is Size lower
				? Translations.GetString ("Smaller ({0})", FormatSize (lower))
				: string.Empty;
			upperButton.Label = upperSize is Size upper
				? Translations.GetString ("Larger ({0})", FormatSize (upper))
				: string.Empty;
			customButton.Label = Translations.GetString ("Custom size");
			lowerButton.Visible = lowerSize is not null;
			upperButton.Visible = upperSize is not null;
			customButton.Visible = imageService == AI.AiRequestSettings.GptImageService;

			if (choice == "lower" && lowerSize is null)
				choice = upperSize is not null ? "upper" : string.Empty;
			if (choice == "upper" && upperSize is null)
				choice = lowerSize is not null ? "lower" : string.Empty;
			if (choice == string.Empty && customButton.Visible)
				choice = "custom";

			updating = true;
			lowerButton.Active = choice == "lower";
			upperButton.Active = choice == "upper";
			customButton.Active = choice == "custom";
			updating = false;
		}

		private void SelectResolution (string selectedChoice)
		{
			if (updating)
				return;
			choice = selectedChoice;
			UpdatePreview ();
		}

		private Size? GetSelectedSize ()
		{
			if (choice == "custom" && customButton.Visible && customButton.Active)
				return new Size ((int) widthSpinner.Value, (int) heightSpinner.Value);
			return choice == "lower" ? lowerSize : choice == "upper" ? upperSize : null;
		}

		private string? GetValidationError (Size? selected)
		{
			if (selected is not Size selectedSize)
				return lowerSize is null && upperSize is null
					? Translations.GetString (
						"No supported {0} resolution can contain the source image {1}.",
						GetImageServiceLabel (imageService),
						FormatSize (sourceSize))
					: Translations.GetString ("Select a valid image size.");
			return choice == "custom" && imageService == AI.AiRequestSettings.GptImageService
				? AI.BackgroundCutoutService.GetGptImageSizeError (selectedSize)
				: null;
		}

		private bool TryGetSelection (out ImageSplitPreviewSelection? selection)
		{
			Size? selected = GetSelectedSize ();
			if (selected is not Size requestSize || GetValidationError (selected) is not null) {
				selection = null;
				return false;
			}
			selection = new ImageSplitPreviewSelection (
				requestSize,
				paddingButton.Active,
				IsDirectMatch
					? CreateSurfacePng (sourceSurface)
					: CreateFittedSourcePng (sourceSurface, requestSize, paddingButton.Active));
			return true;
		}

		private void UpdatePreview ()
		{
			bool directMatch = IsDirectMatch;
			bool custom = choice == "custom" && customButton.Visible && customButton.Active;
			resolutionTitle.Visible = !directMatch;
			resolutionChoices.Visible = !directMatch;
			customGrid.Visible = !directMatch && custom;
			Size? selected = GetSelectedSize ();
			string? error = GetValidationError (selected);
			validationLabel.SetText (error ?? string.Empty);
			validationLabel.Visible = !directMatch && error is not null;
			comparison.Visible = !directMatch;
			singlePreview.Visible = directMatch;
			infoGrid.Visible = !directMatch;
			paddingButton.Visible = !directMatch;
			if (directMatch) {
				ClearAdaptedPreview ();
				adaptedBorder.QueueDraw ();
				return;
			}
			if (selected is not Size requestSize || error is not null) {
				ClearAdaptedPreview ();
				adaptedBackdrop.QueueDraw ();
				adaptedBorder.QueueDraw ();
				return;
			}

			AI.ImageFitInfo fit = AI.BackgroundCutoutService.GetImageFitInfo (sourceSize, requestSize);
			UpdateAdaptedPreview (requestSize);
			adaptedPicture.Visible = true;
			requestSizeLabel.SetText (FormatSize (requestSize));
			scaleLabel.SetText (Translations.GetString ("{0:P1}", fit.Scale));
			paddingLabel.SetText (GetPaddingText (requestSize, fit));
			adaptedBackdrop.QueueDraw ();
			adaptedBorder.QueueDraw ();
		}

		private void UpdateAdaptedPreview (Size requestSize)
		{
			ClearAdaptedPreview ();
			adaptedSurface = CreatePreviewSurface (
				CreateFittedSourcePng (sourceSurface, requestSize, paddingButton.Active));
			adaptedTexture = adaptedSurface.ToTexture ();
			adaptedPicture.Paintable = adaptedTexture;
		}

		private void ClearAdaptedPreview ()
		{
			adaptedPicture.Paintable = sourceTexture;
			adaptedTexture?.Dispose ();
			adaptedTexture = null;
			adaptedSurface?.Dispose ();
			adaptedSurface = null;
		}

		private static byte[] CreateFittedSourcePng (
			Cairo.ImageSurface source,
			Size requestSize,
			bool whitePadding)
		{
			AI.ImageFitInfo fit = AI.BackgroundCutoutService.GetImageFitInfo (
				new Size (source.Width, source.Height),
				requestSize);
			using Cairo.ImageSurface surface = CairoExtensions.CreateImageSurface (
				Cairo.Format.Argb32,
				requestSize.Width,
				requestSize.Height);
			if (whitePadding) {
				using Cairo.Context background = new (surface);
				background.SetSourceColor (new Color (1, 1, 1));
				background.Paint ();
			} else {
				surface.Clear ();
			}

			using Cairo.Context content = new (surface);
			content.Translate (fit.Offset.X, fit.Offset.Y);
			content.Scale (
				fit.ContentSize.Width / (double) source.Width,
				fit.ContentSize.Height / (double) source.Height);
			content.SetSourceSurface (source, 0, 0);
			content.Paint ();
			return CreateSurfacePng (surface);
		}

		private bool IsDirectMatch
			=> GetSelectedSize () is Size selected && selected == sourceSize;
	}
}
