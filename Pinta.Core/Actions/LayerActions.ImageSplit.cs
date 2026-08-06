using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandlePintaCoreActionsLayersImageSplitActivated (object sender, EventArgs e)
	{
		if (cutout_running || !EnsureAiLoggedIn ()
			|| workspace.ActiveDocumentOrDefault is not Document document
			|| !document.Layers.HasSelectedLayer)
			return;

		UserLayer source = document.Layers.CurrentUserLayer;
		if (!source.IsEditable)
			return;

		AiImageRequestOptions? options = await PromptAiImageRequestAsync (
			AiImageRequestMode.ImageSplitGeneration,
			document,
			source);
		if (options is null)
			return;

		ImageSplitPreviewSelection? selection = await ConfirmImageSplitPreviewAsync (source);
		if (selection is null)
			return;

		await GenerateImageAsync (document, options with {
			ImageSize = source.Surface.GetSize (),
			RequestImageSize = selection.RequestSize,
			SourceLayer = source,
			WhitePadding = selection.WhitePadding,
			ParentLayer = source,
		});
	}

	private async Task<ImageSplitPreviewSelection?> ConfirmImageSplitPreviewAsync (UserLayer source)
	{
		Size sourceSize = source.Surface.GetSize ();
		string imageService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
		string provider = AI.AiRequestSettings.GetImageProvider (PintaCore.Settings);
		AI.AiImageResolutionPlan plan = AI.AiImageResolutionPlanner.Create (imageService, provider, sourceSize);
		Size? lowerSize = plan.LowerSize;
		Size? upperSize = plan.UpperSize;
		if (lowerSize is null && upperSize is null) {
			await ShowImageSplitSizeErrorAsync (sourceSize, imageService);
			return null;
		}

		int cost = AI.BackgroundCutoutService.GetImageGenerationCost (provider);
		string costText = cost > 0
			? Translations.GetString ("{0} credits per image", cost)
			: Translations.GetString ("Cost unavailable");
		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("Image Split Preview");
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.DefaultWidth = 980;
		dialog.DefaultHeight = 760;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget confirmButton = dialog.AddButton (
			Translations.GetString ("Confirm and Generate"),
			(int) Gtk.ResponseType.Ok);
		confirmButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.Label serviceValue = Gtk.Label.New (GetImageServiceLabel (imageService));
		serviceValue.Halign = Gtk.Align.Start;
		Gtk.Label providerValue = Gtk.Label.New (provider);
		providerValue.Halign = Gtk.Align.Start;
		Gtk.Label costValue = Gtk.Label.New (costText);
		costValue.Halign = Gtk.Align.Start;
		costValue.AddCssClass (AdwaitaStyles.DimLabel);
		Gtk.Grid settingsGrid = Gtk.Grid.New ();
		settingsGrid.RowSpacing = 6;
		settingsGrid.ColumnSpacing = 12;
		AttachSettingsRow (settingsGrid, Translations.GetString ("Image service:"), serviceValue, 0);
		AttachSettingsRow (settingsGrid, GetProviderLabel (imageService), providerValue, 1);
		AttachSettingsRow (settingsGrid, Translations.GetString ("Generation cost:"), costValue, 2);

		Gtk.CheckButton? lowerButton = lowerSize is Size lower
			? Gtk.CheckButton.NewWithLabel (Translations.GetString ("Smaller ({0})", FormatSize (lower)))
			: null;
		Gtk.CheckButton? upperButton = upperSize is Size upper
			? Gtk.CheckButton.NewWithLabel (Translations.GetString ("Larger ({0})", FormatSize (upper)))
			: null;
		Gtk.CheckButton? customButton = imageService == AI.AiRequestSettings.GptImageService
			? Gtk.CheckButton.NewWithLabel (Translations.GetString ("Custom size"))
			: null;
		Gtk.Box resolutionChoices = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		resolutionChoices.AddCssClass (AdwaitaStyles.Linked);
		AppendResolutionChoice (resolutionChoices, lowerButton);
		AppendResolutionChoice (resolutionChoices, upperButton);
		AppendResolutionChoice (resolutionChoices, customButton);
		if (lowerButton is not null && upperButton is not null)
			upperButton.SetGroup (lowerButton);
		if (customButton is not null) {
			if (upperButton is not null)
				customButton.SetGroup (upperButton);
			else if (lowerButton is not null)
				customButton.SetGroup (lowerButton);
		}

		Gtk.SpinButton widthSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 1);
		Gtk.SpinButton heightSpinner = Gtk.SpinButton.NewWithRange (16, 3840, 1);
		widthSpinner.Value = Math.Clamp (sourceSize.Width, 16, 3840);
		heightSpinner.Value = Math.Clamp (sourceSize.Height, 16, 3840);
		Gtk.Grid customGrid = Gtk.Grid.New ();
		customGrid.ColumnSpacing = 8;
		customGrid.Attach (Gtk.Label.New (Translations.GetString ("Width:")), 0, 0, 1, 1);
		customGrid.Attach (widthSpinner, 1, 0, 1, 1);
		customGrid.Attach (Gtk.Label.New (Translations.GetString ("Height:")), 2, 0, 1, 1);
		customGrid.Attach (heightSpinner, 3, 0, 1, 1);
		Gtk.Label validationLabel = Gtk.Label.New (string.Empty);
		validationLabel.Halign = Gtk.Align.Start;
		validationLabel.Wrap = true;
		validationLabel.AddCssClass (AdwaitaStyles.Error);
		customGrid.Attach (validationLabel, 1, 1, 3, 1);

		Gtk.CheckButton paddingButton = Gtk.CheckButton.NewWithLabel (
			Translations.GetString ("White padding"));
		paddingButton.Active = true;
		Gtk.Label originalSizeLabel = Gtk.Label.New (string.Empty);
		Gtk.Label requestSizeLabel = Gtk.Label.New (string.Empty);
		Gtk.Label scaleLabel = Gtk.Label.New (string.Empty);
		Gtk.Label paddingLabel = Gtk.Label.New (string.Empty);
		foreach (Gtk.Label label in new[] { originalSizeLabel, requestSizeLabel, scaleLabel, paddingLabel })
			label.Halign = Gtk.Align.Start;
		Gtk.Grid infoGrid = Gtk.Grid.New ();
		infoGrid.RowSpacing = 4;
		infoGrid.ColumnSpacing = 12;
		AttachSettingsRow (infoGrid, Translations.GetString ("Original size:"), originalSizeLabel, 0);
		AttachSettingsRow (infoGrid, Translations.GetString ("AI request size:"), requestSizeLabel, 1);
		AttachSettingsRow (infoGrid, Translations.GetString ("Scale:"), scaleLabel, 2);
		AttachSettingsRow (infoGrid, Translations.GetString ("Padding:"), paddingLabel, 3);

		Gtk.Overlay originalPreview = CreatePreviewWidget (
			source.Surface.ToTexture (),
			checkerboard: true,
			out _,
			out _);
		Gtk.Overlay adaptedPreview = CreatePreviewWidget (
			null,
			checkerboard: false,
			out Gtk.Picture adaptedPicture,
			out Gtk.DrawingArea adaptedBackdrop);
		adaptedBackdrop.SetDrawFunc ((_, context, width, height)
			=> DrawPreviewBackground (context, width, height, !paddingButton.Active));
		Gtk.Grid comparison = Gtk.Grid.New ();
		comparison.ColumnSpacing = 12;
		comparison.RowSpacing = 6;
		comparison.Hexpand = true;
		comparison.Vexpand = true;
		comparison.Attach (CreatePreviewHeading ("Original image"), 0, 0, 1, 1);
		comparison.Attach (CreatePreviewHeading ("Adapted image"), 1, 0, 1, 1);
		comparison.Attach (originalPreview, 0, 1, 1, 1);
		comparison.Attach (adaptedPreview, 1, 1, 1, 1);

		Cairo.ImageSurface? adaptedSurface = null;
		string choice = lowerSize is not null ? "lower" : "upper";
		bool updating = false;

		void UpdatePreview ()
		{
			bool custom = customButton?.Active == true;
			customGrid.Visible = custom;
			Size? selected = custom
				? new Size ((int) widthSpinner.Value, (int) heightSpinner.Value)
				: choice == "lower" ? lowerSize : upperSize;
			string? error = selected is Size selectedSize && custom
				&& imageService == AI.AiRequestSettings.GptImageService
				? AI.BackgroundCutoutService.GetGptImageSizeError (selectedSize)
				: null;
			validationLabel.SetText (error ?? string.Empty);
			validationLabel.Visible = error is not null;
			confirmButton.Sensitive = selected is Size && error is null;
			if (selected is not Size requestSize || error is not null)
				return;

			AI.ImageFitInfo fit = AI.BackgroundCutoutService.GetImageFitInfo (sourceSize, requestSize);
			byte[] sourcePng = CreateLayerPng (source);
			byte[] fittedPng = AI.BackgroundCutoutService.FitPng (
				sourcePng,
				requestSize,
				paddingButton.Active,
				out _,
				out _);
			adaptedSurface?.Dispose ();
			adaptedSurface = CreatePreviewSurface (fittedPng);
			adaptedPicture.Paintable = adaptedSurface.ToTexture ();
			originalSizeLabel.SetText (FormatSize (sourceSize));
			requestSizeLabel.SetText (FormatSize (requestSize));
			scaleLabel.SetText (Translations.GetString ("{0:P1}", fit.Scale));
			paddingLabel.SetText (GetPaddingText (requestSize, fit));
			adaptedBackdrop.QueueDraw ();
		}

		void SelectResolution (Gtk.CheckButton button, string value)
		{
			if (updating || !button.Active)
				return;
			updating = true;
			choice = value;
			updating = false;
			UpdatePreview ();
		}

		if (lowerButton is not null)
			lowerButton.OnToggled += (_, _) => SelectResolution (lowerButton, "lower");
		if (upperButton is not null)
			upperButton.OnToggled += (_, _) => SelectResolution (upperButton, "upper");
		if (customButton is not null)
			customButton.OnToggled += (_, _) => SelectResolution (customButton, "custom");
		widthSpinner.OnValueChanged += (_, _) => UpdatePreview ();
		heightSpinner.OnValueChanged += (_, _) => UpdatePreview ();
		paddingButton.OnToggled += (_, _) => {
			paddingButton.Label = Translations.GetString (
				paddingButton.Active ? "White padding" : "Transparent padding");
			UpdatePreview ();
		};
		paddingButton.Label = Translations.GetString ("White padding");
		if (lowerButton is not null)
			lowerButton.Active = choice == "lower";
		else if (upperButton is not null)
			upperButton.Active = true;
		UpdatePreview ();

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 10);
		content.SetAllMargins (16);
		Gtk.Label titleLabel = Gtk.Label.New (Translations.GetString ("Image Split Preview"));
		titleLabel.Halign = Gtk.Align.Start;
		titleLabel.AddCssClass (AdwaitaStyles.Heading);
		content.Append (titleLabel);
		content.Append (settingsGrid);
		content.Append (Gtk.Label.New (Translations.GetString ("Resolution:")));
		content.Append (resolutionChoices);
		content.Append (customGrid);
		content.Append (comparison);
		content.Append (infoGrid);
		content.Append (paddingButton);
		dialog.GetContentAreaBox ().Append (content);

		Gtk.ResponseType response = await dialog.RunAsync ();
		Size? finalSize = customButton?.Active == true
			? new Size ((int) widthSpinner.Value, (int) heightSpinner.Value)
			: choice == "lower" ? lowerSize : upperSize;
		bool valid = finalSize is Size final
			&& (imageService != AI.AiRequestSettings.GptImageService
				|| AI.BackgroundCutoutService.GetGptImageSizeError (final) is null);
		adaptedSurface?.Dispose ();
		dialog.Close ();
		return response == Gtk.ResponseType.Ok && valid && finalSize is Size selectedFinal
			? new ImageSplitPreviewSelection (selectedFinal, paddingButton.Active)
			: null;
	}

	private async Task ShowImageSplitSizeErrorAsync (Size sourceSize, string imageService)
	{
		using Adw.MessageDialog dialog = Adw.MessageDialog.New (
			chrome.MainWindow,
			Translations.GetString ("Image Split Preview"),
			Translations.GetString (
				"No supported {0} resolution can contain the source image {1}.",
				GetImageServiceLabel (imageService),
				FormatSize (sourceSize)));
		dialog.AddResponse ("ok", Translations.GetString ("_OK"));
		dialog.DefaultResponse = "ok";
		await dialog.RunAsync ();
	}

	private static void AttachSettingsRow (Gtk.Grid grid, string label, Gtk.Widget value, int row)
	{
		Gtk.Label labelWidget = Gtk.Label.New (label);
		labelWidget.Halign = Gtk.Align.End;
		grid.Attach (labelWidget, 0, row, 1, 1);
		grid.Attach (value, 1, row, 1, 1);
	}

	private static void AppendResolutionChoice (Gtk.Box box, Gtk.CheckButton? button)
	{
		if (button is null)
			return;
		button.Hexpand = true;
		box.Append (button);
	}

	private static Gtk.Overlay CreatePreviewWidget (
		Gdk.Paintable? paintable,
		bool checkerboard,
		out Gtk.Picture picture,
		out Gtk.DrawingArea background)
	{
		background = Gtk.DrawingArea.New ();
		background.SetSizeRequest (430, 300);
		background.CanTarget = false;
		background.SetDrawFunc ((_, context, width, height)
			=> DrawPreviewBackground (context, width, height, checkerboard));
		picture = Gtk.Picture.New ();
		picture.ContentFit = Gtk.ContentFit.ScaleDown;
		picture.Hexpand = true;
		picture.Vexpand = true;
		picture.Halign = Gtk.Align.Fill;
		picture.Valign = Gtk.Align.Fill;
		picture.CanTarget = false;
		if (paintable is not null)
			picture.Paintable = paintable;
		Gtk.Overlay overlay = Gtk.Overlay.New ();
		overlay.SetChild (background);
		overlay.AddOverlay (picture);
		return overlay;
	}

	private static void DrawPreviewBackground (Context context, int width, int height, bool checkerboard)
	{
		if (width <= 0 || height <= 0)
			return;
		context.SetSourceColor (new Color (1, 1, 1));
		context.Rectangle (0, 0, width, height);
		context.Fill ();
		if (!checkerboard)
			return;
		const int cell = 16;
		for (int y = 0; y < height; y += cell)
			for (int x = 0; x < width; x += cell)
				if ((x / cell + y / cell) % 2 == 0) {
					context.SetSourceColor (new Color (0.88, 0.89, 0.90));
					context.Rectangle (x, y, cell, cell);
					context.Fill ();
				}
	}

	private static string GetPaddingText (Size requestSize, AI.ImageFitInfo fit)
	{
		int right = requestSize.Width - fit.ContentSize.Width - fit.Offset.X;
		int bottom = requestSize.Height - fit.ContentSize.Height - fit.Offset.Y;
		if (fit.Offset == PointI.Zero && right == 0 && bottom == 0)
			return Translations.GetString ("None");
		return Translations.GetString (
			"Left {0}, right {1}, top {2}, bottom {3} px",
			fit.Offset.X,
			right,
			fit.Offset.Y,
			bottom);
	}

	private static string GetImageServiceLabel (string imageService)
		=> imageService switch {
			AI.AiRequestSettings.NanoBananaService => Translations.GetString ("Nano Banana"),
			AI.AiRequestSettings.AgnesService => Translations.GetString ("Agnes"),
			_ => Translations.GetString ("GPT Image"),
		};

	private static string GetProviderLabel (string imageService)
		=> imageService == AI.AiRequestSettings.NanoBananaService
			? Translations.GetString ("Nano Banana channel:")
			: Translations.GetString ("GPT provider:");

	private sealed record ImageSplitPreviewSelection (Size RequestSize, bool WhitePadding);

	private static UserLayer AddAiChildResultLayer (
		Document document,
		UserLayer parent,
		string name,
		Size size)
	{
		UserLayer child = document.Layers.CreateLayer (name, size.Width, size.Height);
		child.Transform = parent.Transform.Clone ();
		document.Layers.Insert (child, new LayerPosition (parent, parent.Children.Count));
		document.Layers.SetCurrentUserLayer (child);
		return child;
	}
}
