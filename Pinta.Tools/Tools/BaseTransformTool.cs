// 
// BaseTransformTool.cs
//  
// Author:
//       Volodymyr <${AuthorEmail}>
// 
// Copyright (c) 2012 Volodymyr
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using Cairo;
using Pinta.Core;

namespace Pinta.Tools;

public abstract class BaseTransformTool : BaseTool
{
	private readonly int rotate_steps = 32;
	private readonly Matrix transform = CairoExtensions.CreateIdentityMatrix ();
	private RectangleD source_rect;
	private PointD original_point;
	private bool is_dragging = false;
	private bool is_rotating = false;
	private bool is_scaling = false;
	private bool is_scaling_handle = false;
	private bool using_mouse = false;
	private readonly IWorkspaceService workspace;
	private readonly RectangleHandle transform_handle;
	private Gtk.CheckButton? show_transform_controls;

	/// <summary>
	/// Initializes a new instance of the <see cref="BaseTransformTool"/> class.
	/// </summary>
	public BaseTransformTool (IServiceProvider services) : base (services)
	{
		workspace = services.GetService<IWorkspaceService> ();
		transform_handle = new (workspace) {
			DrawOutline = true,
			InvertIfNegative = true,
			PreserveAspectRatio = true,
		};
	}

	public override IEnumerable<IToolHandle> Handles => [transform_handle];

	protected bool TransformControlsVisible => show_transform_controls?.Active ?? true;

	protected override void OnBuildToolBar (Gtk.Box toolbar)
	{
		show_transform_controls ??= Gtk.CheckButton.NewWithLabel (Translations.GetString ("Show transform controls"));
		show_transform_controls.Active = Settings.GetSetting (SettingNames.SHOW_TRANSFORM_CONTROLS, true);
		show_transform_controls.OnToggled += (_, _) => {
			UpdateTransformHandle (workspace.HasOpenDocuments ? workspace.ActiveDocument : null);
			workspace.Invalidate ();
		};

		toolbar.Append (show_transform_controls);

		base.OnBuildToolBar (toolbar);
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		if (show_transform_controls is not null)
			settings.PutSetting (SettingNames.SHOW_TRANSFORM_CONTROLS, show_transform_controls.Active);
	}

	protected override void OnMouseDown (
		Document document,
		ToolMouseEventArgs e)
	{
		if (IsActive)
			return;

		original_point = e.PointDouble;

		if (!document.Workspace.PointInCanvas (e.PointDouble))
			return;

		if (e.MouseButton == MouseButton.Left && transform_handle.Active && transform_handle.BeginDrag (e.PointDouble, document.ImageSize))
			is_scaling_handle = true;
		else if (e.MouseButton == MouseButton.Right)
			is_rotating = true;
		else if (e.IsControlPressed)
			is_scaling = true;
		else
			is_dragging = true;

		using_mouse = true;

		OnStartTransform (document);
	}

	protected override void OnMouseMove (
		Document document,
		ToolMouseEventArgs e)
	{
		if (!IsActive || !using_mouse) {
			UpdateTransformHandle (document);
			SetCursor (transform_handle.GetCursorAtPoint (e.WindowPoint) ?? DefaultCursor);
			return;
		}

		bool constrain = e.IsShiftPressed;

		PointD center = source_rect.GetCenter ();

		// The cursor position can be a subpixel value. Round to an integer
		// so that we only translate by entire pixels.
		// (Otherwise, blurring / anti-aliasing may be introduced)

		double dx = Math.Floor (e.PointDouble.X - original_point.X);
		double dy = Math.Floor (e.PointDouble.Y - original_point.Y);

		PointD c1 = original_point - center;
		PointD c2 = e.PointDouble - center;

		RadiansAngle angle = new (Math.Atan2 (c1.Y, c1.X) - Math.Atan2 (c2.Y, c2.X));

		transform.InitIdentity ();

		if (is_scaling_handle) {
			transform_handle.UpdateDrag (e.PointDouble, e.IsShiftPressed);
			RectangleD newRect = transform_handle.Rectangle;

			transform.Translate (newRect.X, newRect.Y);
			transform.Scale (
				newRect.Width / source_rect.Width,
				newRect.Height / source_rect.Height);
			transform.Translate (-source_rect.X, -source_rect.Y);
		} else if (is_scaling) {

			double sx = (c1.X + dx) / c1.X;
			double sy = (c1.Y + dy) / c1.Y;

			if (constrain) {

				double max_scale = Math.Max (Math.Abs (sx), Math.Abs (sy));

				sx = max_scale * Math.Sign (sx);
				sy = max_scale * Math.Sign (sy);
			}

			transform.Translate (center.X, center.Y);
			transform.Scale (sx, sy);
			transform.Translate (-center.X, -center.Y);
		} else if (is_rotating) {

			if (constrain)
				angle = Utility.GetNearestStepAngle (angle, rotate_steps);

			transform.Translate (center.X, center.Y);
			transform.Rotate (-angle.Radians);
			transform.Translate (-center.X, -center.Y);

		} else {
			transform.Translate (dx, dy);
		}

		OnUpdateTransform (document, transform);
		UpdateTransformHandle (document);
	}

	protected override void OnMouseUp (
		Document document,
		ToolMouseEventArgs e)
	{
		if (!IsActive || !using_mouse)
			return;

		OnFinishTransform (document, transform);
	}

	protected override bool OnKeyDown (
		Document document,
		ToolKeyEventArgs e)
	{
		if (using_mouse) // Don't handle the arrow keys while already interacting via the mouse.
			return base.OnKeyDown (document, e);

		double dx = 0.0;
		double dy = 0.0;
		double coeff = e.IsControlPressed ? 10.0 : 1.0;

		switch (e.Key.Value) {
			case Gdk.Constants.KEY_Left:
				dx = -coeff;
				break;
			case Gdk.Constants.KEY_Right:
				dx = coeff;
				break;
			case Gdk.Constants.KEY_Up:
				dy = -coeff;
				break;
			case Gdk.Constants.KEY_Down:
				dy = coeff;
				break;
			default:
				// Otherwise, let the key be handled elsewhere.
				return base.OnKeyDown (document, e);
		}

		if (!IsActive) {
			is_dragging = true;
			OnStartTransform (document);
		}

		transform.Translate (dx, dy);
		OnUpdateTransform (document, transform);
		UpdateTransformHandle (document);

		return true;
	}

	protected override bool OnKeyUp (
		Document document,
		ToolKeyEventArgs e)
	{
		if (IsActive && !using_mouse)
			OnFinishTransform (document, transform);

		return base.OnKeyUp (document, e);
	}

	protected abstract RectangleD GetSourceRectangle (Document document);

	protected virtual void OnStartTransform (Document document)
	{
		source_rect = GetSourceRectangle (document);
		transform.InitIdentity ();
	}

	protected virtual void OnUpdateTransform (
		Document document,
		Matrix transform)
	{ }

	protected virtual void OnFinishTransform (
		Document document,
		Matrix transform)
	{
		is_dragging = false;
		is_rotating = false;
		is_scaling = false;
		is_scaling_handle = false;
		using_mouse = false;
		if (transform_handle.IsDragging)
			transform_handle.EndDrag ();
		UpdateTransformHandle (document);
	}

	private bool IsActive
		=> is_dragging || is_rotating || is_scaling || is_scaling_handle;

	protected override void OnActivated (Document? document)
	{
		base.OnActivated (document);
		workspace.SelectedLayerChanged += HandleTransformTargetChanged;
		workspace.SelectionChanged += HandleTransformTargetChanged;
		UpdateTransformHandle (document);
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		base.OnDeactivated (document, newTool);
		workspace.SelectedLayerChanged -= HandleTransformTargetChanged;
		workspace.SelectionChanged -= HandleTransformTargetChanged;
		transform_handle.Active = false;
	}

	private void HandleTransformTargetChanged (object? sender, EventArgs e)
	{
		if (IsActive)
			return;

		UpdateTransformHandle (workspace.HasOpenDocuments ? workspace.ActiveDocument : null);
		workspace.Invalidate ();
	}

	protected override void OnAfterUndo (Document document)
	{
		base.OnAfterUndo (document);
		UpdateTransformHandle (document);
	}

	protected override void OnAfterRedo (Document document)
	{
		base.OnAfterRedo (document);
		UpdateTransformHandle (document);
	}

	protected virtual bool ShouldShowTransformHandle (Document document)
		=> TransformControlsVisible && document.Selection.Visible;

	protected void UpdateTransformHandle (Document? document)
	{
		if (document is null || !ShouldShowTransformHandle (document)) {
			transform_handle.Active = false;
			return;
		}

		RectangleD rect = GetSourceRectangle (document);
		transform_handle.Rectangle = rect;
		transform_handle.Active = rect.Width > 0 && rect.Height > 0;
	}
}

