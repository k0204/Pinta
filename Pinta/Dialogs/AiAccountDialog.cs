using System.Diagnostics.CodeAnalysis;
using Pinta.Core;

namespace Pinta;

[GObject.Subclass<Gtk.Dialog>]
public sealed partial class AiAccountDialog
{
	private Gtk.Entry api_uri_entry;
	private Gtk.Entry username_entry;
	private Gtk.Entry password_entry;

	[MemberNotNull (nameof (api_uri_entry))]
	[MemberNotNull (nameof (username_entry))]
	[MemberNotNull (nameof (password_entry))]
	partial void Initialize ()
	{
		const int spacing = 6;

		Gtk.Entry apiUriEntry = Gtk.Entry.New ();
		apiUriEntry.Hexpand = true;
		apiUriEntry.Halign = Gtk.Align.Fill;
		apiUriEntry.SetActivatesDefault (true);

		Gtk.Entry usernameEntry = Gtk.Entry.New ();
		usernameEntry.Hexpand = true;
		usernameEntry.Halign = Gtk.Align.Fill;
		usernameEntry.SetActivatesDefault (true);

		Gtk.Entry passwordEntry = Gtk.Entry.New ();
		passwordEntry.Hexpand = true;
		passwordEntry.Halign = Gtk.Align.Fill;
		passwordEntry.Visibility = false;
		passwordEntry.SetActivatesDefault (true);

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = spacing;
		grid.ColumnSpacing = spacing;
		grid.Attach (CreateLabel (Translations.GetString ("API Server:"), Gtk.Align.End), 0, 0, 1, 1);
		grid.Attach (apiUriEntry, 1, 0, 1, 1);
		grid.Attach (CreateLabel (Translations.GetString ("Email:"), Gtk.Align.End), 0, 1, 1, 1);
		grid.Attach (usernameEntry, 1, 1, 1, 1);
		grid.Attach (CreateLabel (Translations.GetString ("Password:"), Gtk.Align.End), 0, 2, 1, 1);
		grid.Attach (passwordEntry, 1, 2, 1, 1);

		Title = Translations.GetString ("AI Account");
		Modal = true;
		DefaultWidth = 420;
		IconName = Resources.StandardIcons.User;

		AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		AddButton (Translations.GetString ("_Register"), (int) Gtk.ResponseType.Apply);
		Gtk.Widget loginButton = AddButton (Translations.GetString ("_Login"), (int) Gtk.ResponseType.Ok);
		loginButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		this.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.Box contentArea = this.GetContentAreaBox ();
		contentArea.Spacing = spacing;
		contentArea.SetAllMargins (12);
		contentArea.Append (grid);

		api_uri_entry = apiUriEntry;
		username_entry = usernameEntry;
		password_entry = passwordEntry;
	}

	public static AiAccountDialog New (Gtk.Window parent, Pinta.Core.AI.AiAuthService auth)
	{
		AiAccountDialog dialog = NewWithProperties ([]);
		dialog.TransientFor = parent;
		dialog.api_uri_entry.SetText (auth.ApiBaseUri);
		dialog.username_entry.SetText (auth.Username);
		return dialog;
	}

	public string ApiBaseUri => api_uri_entry.GetText ().Trim ();
	public string Username => username_entry.GetText ().Trim ();
	public string Password => password_entry.GetText ();

	private static Gtk.Label CreateLabel (string text, Gtk.Align horizontalAlign)
	{
		Gtk.Label result = Gtk.Label.New (text);
		result.Halign = horizontalAlign;
		return result;
	}
}
