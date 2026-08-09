//
// LayerActions.VideoDialog.cs
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private enum VideoGenerationMode
	{
		FirstFrame,
		FirstLastFrame,
		MultiImage,
	}

	private sealed record VideoGenerationRequestOptions (
		string Prompt,
		AI.AiVideoProviderInfo Provider,
		string Model,
		VideoGenerationMode Mode,
		IReadOnlyList<Gio.File> ReferenceFiles,
		string Resolution,
		string Ratio,
		int Duration,
		bool Audio,
		bool Watermark);

	private sealed class VideoDialogControls
	{
		public required UserLayer SourceLayer { get; init; }
		public required IReadOnlyList<AI.AiVideoProviderInfo> Providers { get; init; }
		public required Gtk.ComboBoxText Provider { get; init; }
		public required Gtk.ComboBoxText Model { get; init; }
		public required Gtk.ComboBoxText Mode { get; init; }
		public required Gtk.Label Capability { get; init; }
		public required Gtk.Label Cost { get; init; }
		public required Gtk.Box Preview { get; init; }
		public required Gtk.Button ChooseFiles { get; init; }
		public required Gtk.Label Files { get; init; }
		public required Gtk.TextView Prompt { get; init; }
		public required Gtk.ComboBoxText Resolution { get; init; }
		public required Gtk.Label RatioLabel { get; init; }
		public required Gtk.ComboBoxText Ratio { get; init; }
		public required Gtk.SpinButton Duration { get; init; }
		public required Gtk.CheckButton Audio { get; init; }
		public required Gtk.CheckButton Watermark { get; init; }
		public required Gtk.Widget Submit { get; init; }
		public List<Gio.File> ReferenceFiles { get; } = [];
	}

	private async Task<VideoGenerationRequestOptions?> PromptVideoRequestAsync (UserLayer layer)
	{
		using Gtk.Dialog dialog = CreateVideoDialog ();
		IReadOnlyList<AI.AiVideoProviderInfo> providers = [.. PintaCore.AiProviders.VideoProviders.Where (
			provider => provider.SupportsImageToVideo && provider.Models.Any (IsImageToVideoModel))];
		if (providers.Count == 0)
			throw new InvalidOperationException (Translations.GetString ("No video generation channel is available."));

		VideoDialogControls controls = CreateVideoDialogControls (dialog, layer, providers);
		BuildVideoDialogContent (dialog, controls);
		WireVideoDialogEvents (dialog, controls);
		UpdateVideoModels (controls);

		if (await dialog.RunAsync () != Gtk.ResponseType.Ok)
			return null;
		dialog.Hide ();
		return ReadVideoDialogRequest (controls);
	}

	private PintaDialog CreateVideoDialog ()
	{
		PintaDialog dialog = PintaDialog.NewWithProperties ([]);
		dialog.Title = Translations.GetString ("Generate Video");
		dialog.TransientFor = chrome.MainWindow;
		dialog.DefaultWidth = 760;
		dialog.DefaultHeight = 720;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget submit = dialog.AddButton (Translations.GetString ("_Generate"), (int) Gtk.ResponseType.Ok);
		submit.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);
		return dialog;
	}

	private static VideoDialogControls CreateVideoDialogControls (
		Gtk.Dialog dialog,
		UserLayer layer,
		IReadOnlyList<AI.AiVideoProviderInfo> providers)
	{
		Gtk.ComboBoxText provider = Gtk.ComboBoxText.New ();
		foreach (AI.AiVideoProviderInfo item in providers)
			provider.AppendText (item.Name);
		provider.Active = 0;

		return new () {
			SourceLayer = layer,
			Providers = providers,
			Provider = provider,
			Model = Gtk.ComboBoxText.New (),
			Mode = Gtk.ComboBoxText.New (),
			Capability = CreateDimLabel (),
			Cost = CreateDimLabel (),
			Preview = Gtk.Box.New (Gtk.Orientation.Horizontal, 10),
			ChooseFiles = Gtk.Button.NewWithLabel (Translations.GetString ("Choose Images...")),
			Files = CreateDimLabel (),
			Prompt = CreateVideoPromptView (),
			Resolution = CreateTextCombo (["480P", "720P", "1080P"]),
			RatioLabel = CreateSettingsLabel (Translations.GetString ("Aspect ratio:")),
			Ratio = CreateTextCombo ([Translations.GetString ("Auto"), "16:9", "4:3", "1:1", "3:4", "9:16"]),
			Duration = CreateDurationSpinner (),
			Audio = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Generate audio")),
			Watermark = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Add watermark")),
			Submit = dialog.GetWidgetForResponse ((int) Gtk.ResponseType.Ok)!,
		};
	}

	private static Gtk.Label CreateDimLabel ()
	{
		Gtk.Label label = Gtk.Label.New (string.Empty);
		label.Halign = Gtk.Align.Start;
		label.Wrap = true;
		label.AddCssClass (AdwaitaStyles.DimLabel);
		return label;
	}

	private static Gtk.TextView CreateVideoPromptView ()
	{
		Gtk.TextView prompt = Gtk.TextView.New ();
		prompt.WrapMode = Gtk.WrapMode.WordChar;
		prompt.SetSizeRequest (-1, 120);
		prompt.Buffer!.SetText (string.Empty, -1);
		return prompt;
	}

	private static Gtk.ComboBoxText CreateTextCombo (IEnumerable<string> values)
	{
		Gtk.ComboBoxText combo = Gtk.ComboBoxText.New ();
		foreach (string value in values)
			combo.AppendText (value);
		combo.Active = 0;
		combo.Hexpand = true;
		return combo;
	}

	private static Gtk.SpinButton CreateDurationSpinner ()
	{
		Gtk.SpinButton spinner = Gtk.SpinButton.NewWithRange (2, 30, 1);
		spinner.Value = 5;
		spinner.Hexpand = true;
		return spinner;
	}

	private static void BuildVideoDialogContent (Gtk.Dialog dialog, VideoDialogControls controls)
	{
		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 10);
		content.SetAllMargins (16);
		content.Hexpand = true;
		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.Hexpand = true;
		scroll.Vexpand = true;
		scroll.SetPolicy (Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
		scroll.SetChild (content);
		dialog.GetContentAreaBox ().Append (scroll);

		content.Append (CreateDialogLabel (Translations.GetString ("Model Settings")));
		content.Append (CreateVideoModelGrid (controls));
		content.Append (controls.Capability);
		content.Append (CreateDialogLabel (Translations.GetString ("Input Media")));
		content.Append (CreateVideoPreviewScroll (controls.Preview));
		content.Append (controls.ChooseFiles);
		content.Append (controls.Files);
		content.Append (CreateDialogLabel (Translations.GetString ("Video Prompt")));
		content.Append (controls.Prompt);
		content.Append (CreateDialogLabel (Translations.GetString ("Output Settings")));
		content.Append (CreateVideoOutputGrid (controls));
	}

	private static Gtk.ScrolledWindow CreateVideoPreviewScroll (Gtk.Box preview)
	{
		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.Hexpand = true;
		scroll.SetPolicy (Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
		scroll.SetChild (preview);
		return scroll;
	}

	private static Gtk.Grid CreateVideoModelGrid (VideoDialogControls controls)
	{
		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = 8;
		grid.ColumnSpacing = 8;
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Channel:")), 0, 0, 1, 1);
		grid.Attach (controls.Provider, 1, 0, 1, 1);
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Model:")), 0, 1, 1, 1);
		grid.Attach (controls.Model, 1, 1, 1, 1);
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Generation mode:")), 0, 2, 1, 1);
		grid.Attach (controls.Mode, 1, 2, 1, 1);
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Generation cost:")), 0, 3, 1, 1);
		grid.Attach (controls.Cost, 1, 3, 1, 1);
		return grid;
	}

	private static Gtk.Grid CreateVideoOutputGrid (VideoDialogControls controls)
	{
		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = 8;
		grid.ColumnSpacing = 8;
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Resolution:")), 0, 0, 1, 1);
		grid.Attach (controls.Resolution, 1, 0, 1, 1);
		grid.Attach (controls.RatioLabel, 0, 1, 1, 1);
		grid.Attach (controls.Ratio, 1, 1, 1, 1);
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Duration (seconds):")), 0, 2, 1, 1);
		grid.Attach (controls.Duration, 1, 2, 1, 1);
		grid.Attach (controls.Audio, 1, 3, 1, 1);
		grid.Attach (controls.Watermark, 1, 4, 1, 1);
		return grid;
	}

	private void WireVideoDialogEvents (Gtk.Dialog dialog, VideoDialogControls controls)
	{
		controls.Provider.OnChanged += (_, _) => UpdateVideoModels (controls);
		controls.Model.OnChanged += (_, _) => UpdateVideoModelCapabilities (controls);
		controls.Mode.OnChanged += (_, _) => UpdateVideoInput (controls);
		controls.ChooseFiles.OnClicked += async (_, _) => await ChooseVideoImagesAsync (dialog, controls);
		controls.Prompt.Buffer!.OnChanged += (_, _) => UpdateVideoSubmit (controls);
	}

	private static void UpdateVideoModels (VideoDialogControls controls)
	{
		AI.AiVideoProviderInfo provider = GetSelectedVideoProvider (controls);
		List<string> models = [.. provider.Models.Where (IsImageToVideoModel)];
		controls.Model.RemoveAll ();
		foreach (string model in models)
			controls.Model.AppendText (model);
		controls.Model.Active = Math.Max (0, models.IndexOf (provider.DefaultModel ?? string.Empty));
		controls.Cost.SetText (Translations.GetString ("{0} credits", provider.VideoCost));
		UpdateVideoModelCapabilities (controls);
	}

	private static void UpdateVideoModelCapabilities (VideoDialogControls controls)
	{
		controls.Mode.RemoveAll ();
		controls.Mode.AppendText (Translations.GetString ("First frame"));
		controls.Mode.AppendText (Translations.GetString ("First and last frames"));
		controls.Mode.AppendText (Translations.GetString ("Multiple images"));
		controls.Capability.SetText (Translations.GetString ("Supports a first frame, first and last frames, or up to 10 reference images."));
		controls.Mode.Active = 0;
		UpdateVideoInput (controls);
	}

	private static void UpdateVideoInput (VideoDialogControls controls)
	{
		VideoGenerationMode mode = GetSelectedVideoMode (controls);
		controls.ReferenceFiles.Clear ();
		controls.ChooseFiles.Visible = mode is VideoGenerationMode.FirstLastFrame or VideoGenerationMode.MultiImage;
		controls.ChooseFiles.SetLabel (mode == VideoGenerationMode.FirstLastFrame
			? Translations.GetString ("Choose Last Frame...")
			: Translations.GetString ("Choose Reference Images..."));
		controls.Files.Visible = controls.ChooseFiles.Visible;
		controls.Files.SetText (mode == VideoGenerationMode.FirstLastFrame
			? Translations.GetString ("No last frame selected")
			: Translations.GetString ("No reference images selected"));
		RebuildVideoPreviews (controls);
		UpdateVideoSubmit (controls);
	}

	private async Task ChooseVideoImagesAsync (Gtk.Dialog dialog, VideoDialogControls controls)
	{
		using Gtk.FileFilter filter = CreateImagesFileFilter ();
		using Gio.ListStore filters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
		filters.Append (filter);
		using Gtk.FileDialog picker = Gtk.FileDialog.New ();
		picker.SetTitle (Translations.GetString ("Choose Reference Images"));
		picker.SetFilters (filters);
		if (recent_files.GetDialogDirectory () is Gio.File directory && directory.QueryExists (null))
			picker.SetInitialFolder (directory);

		IReadOnlyList<Gio.File>? choices = GetSelectedVideoMode (controls) == VideoGenerationMode.FirstLastFrame
			? await OpenSingleVideoImageAsync (picker, dialog)
			: await picker.OpenFilesAsync (dialog);
		if (choices is null)
			return;
		controls.ReferenceFiles.Clear ();
		controls.ReferenceFiles.AddRange (choices.Take (9));
		controls.Files.SetText (Translations.GetString ("{0} image(s) selected", controls.ReferenceFiles.Count));
		if (controls.ReferenceFiles.Count > 0 && controls.ReferenceFiles[0].GetParent () is Gio.File parent)
			recent_files.LastDialogDirectory = parent;
		RebuildVideoPreviews (controls);
		UpdateVideoSubmit (controls);
	}

	private static async Task<IReadOnlyList<Gio.File>?> OpenSingleVideoImageAsync (
		Gtk.FileDialog picker,
		Gtk.Window dialog)
	{
		Gio.File? file = await picker.OpenAsync (dialog);
		return file is null ? null : [file];
	}

	private static void RebuildVideoPreviews (VideoDialogControls controls)
	{
		while (controls.Preview.GetFirstChild () is Gtk.Widget child)
			controls.Preview.Remove (child);

		controls.Preview.Append (CreateVideoPreviewCard (
			CreateLayerPreview (controls.SourceLayer),
			Translations.GetString ("Current layer"),
			controls.SourceLayer.Name));
		for (int index = 0; index < controls.ReferenceFiles.Count; index++)
			controls.Preview.Append (CreateVideoPreviewCard (
				CreateFilePreview (controls.ReferenceFiles[index]),
				GetSelectedVideoMode (controls) == VideoGenerationMode.FirstLastFrame
					? Translations.GetString ("Last frame")
					: Translations.GetString ("Reference {0}", index + 2),
				controls.ReferenceFiles[index].GetDisplayName ()));
	}

	private static Gtk.Picture CreateLayerPreview (UserLayer layer)
	{
		Gtk.Picture picture = CreateEmptyVideoPreview ();
		picture.Paintable = layer.Surface.ToTexture ();
		return picture;
	}

	private static Gtk.Picture CreateFilePreview (Gio.File file)
	{
		(byte[] png, _) = LoadReferenceImage (file);
		using Cairo.ImageSurface surface = CreatePreviewSurface (png);
		Gtk.Picture picture = CreateEmptyVideoPreview ();
		picture.Paintable = surface.ToTexture ();
		return picture;
	}

	private static Gtk.Picture CreateEmptyVideoPreview ()
	{
		Gtk.Picture picture = Gtk.Picture.New ();
		picture.ContentFit = Gtk.ContentFit.ScaleDown;
		picture.SetSizeRequest (150, 96);
		return picture;
	}

	private static Gtk.Widget CreateVideoPreviewCard (Gtk.Picture picture, string title, string details)
	{
		Gtk.Label titleLabel = Gtk.Label.New (title);
		titleLabel.Halign = Gtk.Align.Start;
		Gtk.Label detailsLabel = CreateDimLabel ();
		detailsLabel.SetText (details);
		detailsLabel.Ellipsize = Pango.EllipsizeMode.End;
		Gtk.Box card = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
		card.SetSizeRequest (170, -1);
		card.SetAllMargins (8);
		card.AddCssClass ("card");
		card.Append (picture);
		card.Append (titleLabel);
		card.Append (detailsLabel);
		return card;
	}

	private static void UpdateVideoSubmit (VideoDialogControls controls)
	{
		VideoGenerationMode mode = GetSelectedVideoMode (controls);
		bool inputValid = mode switch {
			VideoGenerationMode.FirstFrame => true,
			VideoGenerationMode.FirstLastFrame => controls.ReferenceFiles.Count == 1,
			VideoGenerationMode.MultiImage => controls.ReferenceFiles.Count is >= 1 and <= 9,
			_ => false,
		};
		controls.Prompt.Buffer!.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
		controls.Submit.Sensitive = inputValid
			&& !string.IsNullOrWhiteSpace (controls.Prompt.Buffer.GetText (start, end, true));
	}

	private static VideoGenerationRequestOptions ReadVideoDialogRequest (VideoDialogControls controls)
	{
		controls.Prompt.Buffer!.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
		return new (
			controls.Prompt.Buffer.GetText (start, end, true).Trim (),
			GetSelectedVideoProvider (controls),
			GetSelectedVideoModel (controls),
			GetSelectedVideoMode (controls),
			[.. controls.ReferenceFiles],
			controls.Resolution.GetActiveText () ?? "720P",
			controls.Ratio.Active == 0 ? "auto" : controls.Ratio.GetActiveText () ?? "auto",
			(int) controls.Duration.Value,
			controls.Audio.Active,
			controls.Watermark.Active);
	}

	private static AI.AiVideoProviderInfo GetSelectedVideoProvider (VideoDialogControls controls)
		=> controls.Provider.Active >= 0 && controls.Provider.Active < controls.Providers.Count
			? controls.Providers[controls.Provider.Active]
			: throw new InvalidOperationException ("No video provider is selected.");

	private static string GetSelectedVideoModel (VideoDialogControls controls)
		=> controls.Model.GetActiveText () ?? string.Empty;

	private static VideoGenerationMode GetSelectedVideoMode (VideoDialogControls controls)
		=> controls.Mode.Active switch {
			1 => VideoGenerationMode.FirstLastFrame,
			2 => VideoGenerationMode.MultiImage,
			_ => VideoGenerationMode.FirstFrame,
		};

	private static bool IsImageToVideoModel (string model)
		=> !model.Contains ("-r2v-", StringComparison.OrdinalIgnoreCase);
}
