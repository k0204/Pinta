using System;
using System.Collections.Generic;

namespace Pinta.Core;

internal sealed partial class SpritesheetSplitDialog
{
	private readonly List<FramePositionChange> position_history = [];
	private int position_history_pointer = -1;
	private bool frame_position_dragging;

	private readonly record struct FramePositionChange (
		int FrameIndex,
		int OldX,
		int OldY,
		int NewX,
		int NewY);

	private void BeginFramePositionDrag ()
	{
		frame_position_dragging = true;
	}

	private void EndFramePositionDrag (int frameIndex)
	{
		if (!frame_position_dragging || frameIndex < 0 || frameIndex >= frames.Count) {
			frame_position_dragging = false;
			return;
		}

		frame_position_dragging = false;
		RecordPositionChange (
			frameIndex,
			drag_start_x,
			drag_start_y,
			frames[frameIndex].X,
			frames[frameIndex].Y);
	}

	private void RecordPositionChange (int frameIndex, int oldX, int oldY, int newX, int newY)
	{
		if (oldX == newX && oldY == newY)
			return;

		if (position_history_pointer < position_history.Count - 1)
			position_history.RemoveRange (position_history_pointer + 1, position_history.Count - position_history_pointer - 1);

		position_history.Add (new FramePositionChange (frameIndex, oldX, oldY, newX, newY));
		position_history_pointer++;
		UpdatePositionHistoryButtons ();
	}

	private void UndoFramePosition ()
	{
		if (position_history_pointer < 0)
			return;

		FramePositionChange change = position_history[position_history_pointer--];
		ApplyFramePosition (change.FrameIndex, change.OldX, change.OldY);
		UpdatePositionHistoryButtons ();
	}

	private void RedoFramePosition ()
	{
		if (position_history_pointer >= position_history.Count - 1)
			return;

		FramePositionChange change = position_history[++position_history_pointer];
		ApplyFramePosition (change.FrameIndex, change.NewX, change.NewY);
		UpdatePositionHistoryButtons ();
	}

	private void ApplyFramePosition (int frameIndex, int x, int y)
	{
		if (frameIndex < 0 || frameIndex >= frames.Count)
			return;

		EditableFrame frame = frames[frameIndex];
		frame.X = x;
		frame.Y = y;
		if (frame.AnchorX is not null && frame.AnchorY is not null) {
			frame.AnchorX = canvas_width.Value / 2.0 - x;
			frame.AnchorY = canvas_height.Value - y;
		}

		if (frameIndex == selected_frame) {
			syncing = true;
			frame_x.Value = x;
			frame_y.Value = y;
			syncing = false;
		}
		Refresh ();
	}

	private void UpdatePositionHistoryButtons ()
	{
		undo_position.Sensitive = position_history_pointer >= 0;
		redo_position.Sensitive = position_history_pointer < position_history.Count - 1;
	}

	private void ClearPositionHistory ()
	{
		position_history.Clear ();
		position_history_pointer = -1;
		frame_position_dragging = false;
		UpdatePositionHistoryButtons ();
	}

	private bool HandlePositionHistoryKeyPressed (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		if (!args.State.IsControlPressed ())
			return false;

		uint key = args.GetKey ().ToUpper ().Value;
		if (key != Gdk.Constants.KEY_Z && key != Gdk.Constants.KEY_Y)
			return false;

		if (key == Gdk.Constants.KEY_Y || args.State.IsShiftPressed ())
			RedoFramePosition ();
		else
			UndoFramePosition ();
		return true;
	}

	private bool HandlePreviewArrowKeyPressed (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		if (frames.Count == 0)
			return false;

		(int deltaX, int deltaY) = args.GetKey ().Name () switch {
			"Left" => (-1, 0),
			"Right" => (1, 0),
			"Up" => (0, -1),
			"Down" => (0, 1),
			_ => (0, 0),
		};
		if (deltaX == 0 && deltaY == 0)
			return false;

		int step = args.State.IsShiftPressed () ? 10 : 1;
		MoveSelectedFrame (deltaX * step, deltaY * step);
		return true;
	}

	private void MoveSelectedFrame (int deltaX, int deltaY)
	{
		if (selected_frame < 0 || selected_frame >= frames.Count)
			return;

		EditableFrame frame = frames[selected_frame];
		int oldX = frame.X;
		int oldY = frame.Y;
		ApplyFramePosition (selected_frame, oldX + deltaX, oldY + deltaY);
		RecordPositionChange (selected_frame, oldX, oldY, frame.X, frame.Y);
	}
}
