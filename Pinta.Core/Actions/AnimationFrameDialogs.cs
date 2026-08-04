using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal abstract class AnimationFrameDialogWindow : IDisposable
{
	private readonly Gtk.Dialog dialog;
	private readonly AnimationFrameEditor editor;

	protected AnimationFrameDialogWindow (
		Gtk.Window parent,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		IReadOnlyList<UserLayer> outputAttempts,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> saveAnalysis,
		SpritesheetSplitData? savedAnalysis,
		IReadOnlyList<ImageSurface>? frameSurfaces,
		IReadOnlyList<SpritesheetFrameSplit>? existingFrames,
		bool editing,
		string createTitle,
		string editTitle)
	{
		dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString (editing ? editTitle : createTitle);
		dialog.TransientFor = parent;
		dialog.Modal = true;
		dialog.DefaultWidth = 1440;
		dialog.DefaultHeight = 820;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget submit = dialog.AddButton (
			Translations.GetString (editing ? "Save" : "Create"),
			(int) Gtk.ResponseType.Ok);
		submit.AddCssClass (AdwaitaStyles.SuggestedAction);

		Gtk.Button fullscreen = Gtk.Button.NewFromIconName (Resources.StandardIcons.WindowMaximize);
		fullscreen.SetTooltipText (Translations.GetString ("Maximize dialog"));
		fullscreen.AddCssClass (AdwaitaStyles.Flat);
		bool fullscreened = false;
		fullscreen.OnClicked += (_, _) => {
			fullscreened = !fullscreened;
			if (fullscreened)
				dialog.Fullscreen ();
			else
				dialog.Unfullscreen ();
		};
		Gtk.HeaderBar headerBar = Gtk.HeaderBar.New ();
		headerBar.TitleWidget = Gtk.Label.New (dialog.Title);
		headerBar.PackEnd (fullscreen);
		dialog.SetTitlebar (headerBar);

		Gtk.EventControllerKey keys = Gtk.EventControllerKey.New ();
		keys.PropagationPhase = Gtk.PropagationPhase.Capture;
		keys.OnKeyPressed += (_, args) => {
			if (!fullscreened || args.GetKey ().Value != Gdk.Constants.KEY_Escape)
				return false;

			fullscreened = false;
			dialog.Unfullscreen ();
			return true;
		};
		dialog.AddController (keys);

		editor = new AnimationFrameEditor (
			dialog,
			value => submit.Sensitive = value,
			source,
			info,
			outputAttempts,
			analyze,
			saveAnalysis,
			savedAnalysis,
			frameSurfaces,
			existingFrames);
		dialog.GetContentAreaBox ().Append (editor.Content);
	}

	public UserLayer? OutputAttempt => editor.OutputAttempt;

	public async Task<SpritesheetSplitData?> RunAsync ()
	{
		Gtk.ResponseType response = await dialog.RunAsync ();
		dialog.Close ();
		return response == Gtk.ResponseType.Ok ? editor.ReadOptions () : null;
	}

	public void Dispose () => dialog.Dispose ();
}

internal sealed class MultiDirectionAnimationDialog : AnimationFrameDialogWindow
{
	public MultiDirectionAnimationDialog (
		Gtk.Window parent,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		IReadOnlyList<UserLayer> outputAttempts,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> saveAnalysis,
		SpritesheetSplitData? savedAnalysis,
		IReadOnlyList<ImageSurface>? frameSurfaces = null,
		IReadOnlyList<SpritesheetFrameSplit>? existingFrames = null,
		bool editing = false)
		: base (
			parent,
			source,
			info,
			outputAttempts,
			analyze,
			saveAnalysis,
			savedAnalysis,
			frameSurfaces,
			existingFrames,
			editing,
			"Create Multi-Direction Animation",
			"Edit Multi-Direction Animation")
	{
	}
}

internal sealed class SingleDirectionAnimationDialog : AnimationFrameDialogWindow
{
	public SingleDirectionAnimationDialog (
		Gtk.Window parent,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		IReadOnlyList<UserLayer> outputAttempts,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> saveAnalysis,
		SpritesheetSplitData? savedAnalysis,
		IReadOnlyList<ImageSurface>? frameSurfaces = null,
		IReadOnlyList<SpritesheetFrameSplit>? existingFrames = null,
		bool editing = false)
		: base (
			parent,
			source,
			info,
			outputAttempts,
			analyze,
			saveAnalysis,
			savedAnalysis,
			frameSurfaces,
			existingFrames,
			editing,
			"Create Single-Direction Animation",
			"Edit Single-Direction Animation")
	{
	}
}
