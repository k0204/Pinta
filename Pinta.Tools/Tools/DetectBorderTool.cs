using System;
using System.Collections.Generic;
using Pinta.Core;

namespace Pinta.Tools;

public sealed partial class DetectBorderTool : SelectTool
{
	private readonly RecognitionButtonsHandle recognition_buttons;
	private Gtk.SpinButton? minimum_area_spinner;
	private RecognitionAction pressed_action;

	public DetectBorderTool (IServiceProvider services) : base (services)
	{
		DefaultCursor = Gdk.Cursor.NewFromTexture (Resources.GetIcon ("Cursor.RectangleSelect.png"), 9, 18, null);
		recognition_buttons = new (
			services.GetService<IWorkspaceService> (),
			services.GetService<IChromeService> (),
			Resources.GetIcon (Pinta.Resources.Icons.EffectsStylizeOutline));
	}

	public override string Name => Translations.GetString ("Detect Border");
	public override string Icon => Pinta.Resources.Icons.EffectsStylizeOutline;
	public override string StatusBarText => Translations.GetString ("Select an area before detecting the border.");
	public override Gdk.Cursor DefaultCursor { get; }
	public override int Priority => 1;
	public override IEnumerable<IToolHandle> Handles => [.. base.Handles, recognition_buttons];

	protected override void OnBuildToolBar (Gtk.Box toolbar)
	{
		base.OnBuildToolBar (toolbar);
		toolbar.Append (GtkExtensions.CreateToolBarSeparator ());
		toolbar.Append (Gtk.Label.New ($" {Translations.GetString ("Minimum Region Area (%):")} "));
		minimum_area_spinner = GtkExtensions.CreateToolBarSpinButton (
			1,
			100,
			1,
			Settings.GetSetting (Pinta.Core.SettingNames.DETECT_BORDER_MINIMUM_AREA_PERCENT, 1));
		minimum_area_spinner.OnValueChanged += (_, _) => Settings.PutSetting (
			Pinta.Core.SettingNames.DETECT_BORDER_MINIMUM_AREA_PERCENT,
			minimum_area_spinner.GetValueAsInt ());
		toolbar.Append (minimum_area_spinner);
	}

	protected override void DrawShape (Document document, RectangleD rectangle, Layer layer)
	{
		document.Selection.CreateRectangleSelection (rectangle);
	}

	protected override void OnSelectionCompleted (Document document)
	{
		UpdateRecognitionButton (document);
	}

	protected override void OnActivated (Document? document)
	{
		base.OnActivated (document);
		if (document is not null)
			UpdateRecognitionButton (document);
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		recognition_buttons.Active = false;
		base.OnDeactivated (document, newTool);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		pressed_action = recognition_buttons.GetAction (e.WindowPoint);
		if (pressed_action != RecognitionAction.None)
			return;

		recognition_buttons.Active = false;
		base.OnMouseDown (document, e);
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (pressed_action != RecognitionAction.None) {
			RecognitionAction released_action = recognition_buttons.GetAction (e.WindowPoint);
			RecognitionAction action = pressed_action;
			pressed_action = RecognitionAction.None;

			if (released_action == action) {
				if (action == RecognitionAction.Recognize) {
					recognition_buttons.Active = false;
					document.Workspace.Invalidate ();
					PintaCore.Actions.Layers.DetectBorder.Activate ();
				} else {
					recognition_buttons.Active = false;
					PintaCore.Actions.Edit.Deselect.Activate ();
				}
			}
			return;
		}

		base.OnMouseUp (document, e);
	}

	private void UpdateRecognitionButton (Document document)
	{
		RectangleD bounds = document.Selection.HandleBounds;
		recognition_buttons.CanvasPosition = bounds.EndLocation ();
		recognition_buttons.Active = document.Selection.Visible && bounds.Width > 0 && bounds.Height > 0;
		document.Workspace.Invalidate ();
	}

	private enum RecognitionAction
	{
		None,
		Recognize,
		Cancel,
	}

	private sealed class RecognitionButtonsHandle : IToolHandle
	{
		private const float RECOGNIZE_BUTTON_WIDTH = 112;
		private const float CANCEL_BUTTON_WIDTH = 68;
		private const float BUTTON_HEIGHT = 32;
		private const float BUTTON_GAP = 6;
		private readonly IWorkspaceService workspace;
		private readonly Gdk.Texture icon;
		private readonly Pango.Layout recognize_label;
		private readonly Pango.Layout cancel_label;

		public RecognitionButtonsHandle (IWorkspaceService workspace, IChromeService chrome, Gdk.Texture icon)
		{
			this.workspace = workspace;
			this.icon = icon;
			recognize_label = CreateLabel (chrome, Translations.GetString ("Recognize"));
			cancel_label = CreateLabel (chrome, Translations.GetString ("Cancel"));
		}

		public bool Active { get; set; }
		public PointD CanvasPosition { get; set; }

		public bool ContainsPoint (PointD windowPoint)
			=> GetAction (windowPoint) != RecognitionAction.None;

		public RecognitionAction GetAction (PointD windowPoint)
		{
			if (!Active)
				return RecognitionAction.None;

			ComputeWindowRects (out RectangleD recognizeBounds, out RectangleD cancelBounds);
			if (recognizeBounds.ContainsPoint (windowPoint))
				return RecognitionAction.Recognize;
			if (cancelBounds.ContainsPoint (windowPoint))
				return RecognitionAction.Cancel;
			return RecognitionAction.None;
		}

		public void Draw (Gtk.Snapshot snapshot)
		{
			ComputeWindowRects (out RectangleD recognizeBounds, out RectangleD cancelBounds);
			AppendButton (snapshot, recognizeBounds, RECOGNIZE_BUTTON_WIDTH, new Gdk.RGBA { Red = 0.85f, Green = 0.2f, Blue = 0.15f, Alpha = 1 });
			AppendButton (snapshot, cancelBounds, CANCEL_BUTTON_WIDTH, new Gdk.RGBA { Red = 0.25f, Green = 0.27f, Blue = 0.3f, Alpha = 1 });

			Graphene.Rect iconBounds = Graphene.Rect.Alloc ();
			iconBounds.Init ((float) recognizeBounds.X + 7, (float) recognizeBounds.Y + 7, 18, 18);
			snapshot.AppendTexture (icon, iconBounds);

			AppendLabel (snapshot, recognize_label, recognizeBounds.X + 30, recognizeBounds.Y + 7);
			AppendLabel (snapshot, cancel_label, cancelBounds.X + 18, cancelBounds.Y + 7);
		}

		private static Pango.Layout CreateLabel (IChromeService chrome, string text)
		{
			Pango.Layout layout = Pango.Layout.New (chrome.MainWindow.GetPangoContext ());
			layout.SetFontDescription (Pango.FontDescription.FromString ("Sans Bold 10"));
			layout.SetText (text, -1);
			return layout;
		}

		private static void AppendButton (Gtk.Snapshot snapshot, RectangleD bounds, float width, Gdk.RGBA color)
		{
			Graphene.Rect border = Graphene.Rect.Alloc ();
			border.Init ((float) bounds.X, (float) bounds.Y, width, BUTTON_HEIGHT);
			Graphene.Rect body = Graphene.Rect.Alloc ();
			body.Init ((float) bounds.X + 1, (float) bounds.Y + 1, width - 2, BUTTON_HEIGHT - 2);
			snapshot.AppendColor (new Gdk.RGBA { Red = 1, Green = 1, Blue = 1, Alpha = 1 }, border);
			snapshot.AppendColor (color, body);
		}

		private static void AppendLabel (Gtk.Snapshot snapshot, Pango.Layout layout, double x, double y)
		{
			Graphene.Point offset = Graphene.Point.Alloc ();
			offset.Init ((float) x, (float) y);
			snapshot.Save ();
			snapshot.Translate (offset);
			snapshot.AppendLayout (layout, new Gdk.RGBA { Red = 1, Green = 1, Blue = 1, Alpha = 1 });
			snapshot.Restore ();
		}

		private void ComputeWindowRects (out RectangleD recognizeBounds, out RectangleD cancelBounds)
		{
			PointD anchor = workspace.CanvasPointToView (CanvasPosition);
			double y = anchor.Y - BUTTON_HEIGHT - 6;
			cancelBounds = new (anchor.X - CANCEL_BUTTON_WIDTH - 6, y, CANCEL_BUTTON_WIDTH, BUTTON_HEIGHT);
			recognizeBounds = new (cancelBounds.X - BUTTON_GAP - RECOGNIZE_BUTTON_WIDTH, y, RECOGNIZE_BUTTON_WIDTH, BUTTON_HEIGHT);
		}
	}
}
