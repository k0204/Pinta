using System.Diagnostics.CodeAnalysis;
using Pinta.Core;
using Pinta.Core.AI;

namespace Pinta;

[GObject.Subclass<Gtk.Dialog>]
public sealed partial class AiRequestSettingsDialog
{
	private Gtk.ComboBoxText image_service_combobox;
	private Gtk.ComboBoxText gpt_provider_combobox;

	[MemberNotNull (nameof (image_service_combobox))]
	[MemberNotNull (nameof (gpt_provider_combobox))]
	partial void Initialize ()
	{
		const int spacing = 8;

		Gtk.ComboBoxText imageServiceCombobox = CreateCombobox (
			Translations.GetString ("Agnes"),
			Translations.GetString ("GPT Image"));
		Gtk.ComboBoxText gptProviderCombobox = CreateCombobox (
			AiRequestSettings.ZzswitchProvider,
			AiRequestSettings.LukyfaceProvider);
		Gtk.Label gptProviderLabel = CreateLabel (Translations.GetString ("GPT provider:"));
		gptProviderLabel.Visible = false;
		gptProviderCombobox.Visible = false;

		imageServiceCombobox.OnChanged += (_, _) => {
			bool visible = imageServiceCombobox.Active == 1;
			gptProviderLabel.Visible = visible;
			gptProviderCombobox.Visible = visible;
		};

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = spacing;
		grid.ColumnSpacing = spacing;
		grid.Attach (CreateLabel (Translations.GetString ("Image service:")), 0, 0, 1, 1);
		grid.Attach (imageServiceCombobox, 1, 0, 1, 1);
		grid.Attach (gptProviderLabel, 0, 1, 1, 1);
		grid.Attach (gptProviderCombobox, 1, 1, 1, 1);

		Title = Translations.GetString ("AI Request Settings");
		Modal = true;
		DefaultWidth = 420;
		IconName = "preferences-system-symbolic";

		AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget saveButton = AddButton (Translations.GetString ("_Save"), (int) Gtk.ResponseType.Ok);
		saveButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		this.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.Box contentArea = this.GetContentAreaBox ();
		contentArea.Spacing = spacing;
		contentArea.SetAllMargins (12);
		contentArea.Append (grid);

		image_service_combobox = imageServiceCombobox;
		gpt_provider_combobox = gptProviderCombobox;
	}

	internal static AiRequestSettingsDialog New (Gtk.Window parent, ISettingsService settings)
	{
		AiRequestSettingsDialog dialog = NewWithProperties ([]);
		dialog.TransientFor = parent;
		dialog.image_service_combobox.Active =
			AiRequestSettings.GetImageService (settings) == AiRequestSettings.AgnesService ? 0 : 1;
		dialog.gpt_provider_combobox.Active =
			AiRequestSettings.GetGptProvider (settings) == AiRequestSettings.ZzswitchProvider ? 0 : 1;
		return dialog;
	}

	internal void Save (ISettingsService settings)
	{
		string imageService = image_service_combobox.Active == 0
			? AiRequestSettings.AgnesService
			: AiRequestSettings.GptImageService;
		string gptProvider = gpt_provider_combobox.Active == 0
			? AiRequestSettings.ZzswitchProvider
			: AiRequestSettings.LukyfaceProvider;
		AiRequestSettings.Save (settings, imageService, gptProvider);
	}

	private static Gtk.ComboBoxText CreateCombobox (params string[] items)
	{
		Gtk.ComboBoxText result = Gtk.ComboBoxText.New ();
		result.Hexpand = true;
		result.Halign = Gtk.Align.Fill;
		foreach (string item in items)
			result.AppendText (item);
		result.Active = 0;
		return result;
	}

	private static Gtk.Label CreateLabel (string text)
	{
		Gtk.Label result = Gtk.Label.New (text);
		result.Halign = Gtk.Align.End;
		return result;
	}
}
