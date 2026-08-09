using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal abstract class AnimationFrameDialogWindow : IDisposable
{
	private readonly PintaDialog dialog;
	private readonly AnimationFrameEditorBase editor;

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
		string editTitle,
		Func<
			Gtk.Window,
			Action<bool>,
			UserLayer,
			AI.SpritesheetAttemptInfo,
			IReadOnlyList<UserLayer>,
			Func<string, Task<AI.SpriteSegmentationAnalysis>>,
			Action<SpritesheetSplitData>,
			SpritesheetSplitData?,
			IReadOnlyList<ImageSurface>?,
			IReadOnlyList<SpritesheetFrameSplit>?,
			bool,
			AnimationFrameEditorBase> createEditor,
		bool allowAiAnalysis = true)
	{
		dialog = PintaDialog.NewWithProperties ([]);
		dialog.Title = Translations.GetString (editing ? editTitle : createTitle);
		dialog.TransientFor = parent;
		dialog.DefaultWidth = 1440;
		dialog.DefaultHeight = 820;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget submit = dialog.AddButton (
			Translations.GetString (editing ? "Save" : "Create"),
			(int) Gtk.ResponseType.Ok);
		submit.AddCssClass (AdwaitaStyles.SuggestedAction);

		editor = createEditor (
			dialog,
			value => submit.Sensitive = value,
			source,
			info,
			outputAttempts,
			analyze,
			saveAnalysis,
			savedAnalysis,
			frameSurfaces,
			existingFrames,
			allowAiAnalysis);
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
		bool editing = false,
		bool allowAiAnalysis = true)
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
			"Edit Multi-Direction Animation",
			CreateEditor,
			allowAiAnalysis)
	{
	}

	private static AnimationFrameEditorBase CreateEditor (
		Gtk.Window dialog,
		Action<bool> submit,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		IReadOnlyList<UserLayer> attempts,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> save,
		SpritesheetSplitData? saved,
		IReadOnlyList<ImageSurface>? surfaces,
		IReadOnlyList<SpritesheetFrameSplit>? frames,
		bool allowAiAnalysis)
		=> new MultiDirectionAnimationEditor (dialog, submit, source, info, attempts, analyze, save, saved, surfaces, frames, allowAiAnalysis);
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
		bool editing = false,
		bool allowAiAnalysis = true)
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
			"Edit Single-Direction Animation",
			CreateEditor,
			allowAiAnalysis)
	{
	}

	private static AnimationFrameEditorBase CreateEditor (
		Gtk.Window dialog,
		Action<bool> submit,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		IReadOnlyList<UserLayer> attempts,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> save,
		SpritesheetSplitData? saved,
		IReadOnlyList<ImageSurface>? surfaces,
		IReadOnlyList<SpritesheetFrameSplit>? frames,
		bool allowAiAnalysis)
		=> new SingleDirectionAnimationEditor (dialog, submit, source, info, attempts, analyze, save, saved, surfaces, frames, allowAiAnalysis);
}
