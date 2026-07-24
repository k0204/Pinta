using Cairo;

namespace Pinta.Core;

public sealed class UpdateLayerReferenceHistoryItem : BaseHistoryItem
{
	private readonly Document document;
	private readonly UserLayer layer;
	private readonly string? old_reference_path;
	private readonly string? new_reference_path;
	private ImageSurface? embedded_surface;

	public UpdateLayerReferenceHistoryItem (Document document, UserLayer layer, string? oldReferencePath, string? newReferencePath)
		: base (Resources.Icons.LayerImport, Translations.GetString ("Unlock Referenced Layer"))
	{
		this.document = document;
		this.layer = layer;
		old_reference_path = oldReferencePath;
		new_reference_path = newReferencePath;
	}

	public override void Undo ()
	{
		embedded_surface?.Dispose ();
		embedded_surface = layer.Surface.Clone ();
		SetReferencePath (old_reference_path);
	}

	public override void Redo ()
	{
		SetReferencePath (new_reference_path);
		if (new_reference_path is null && embedded_surface is not null) {
			layer.Surface.Clear ();
			using Context context = new (layer.Surface);
			context.SetSourceSurface (embedded_surface, 0, 0);
			context.Paint ();
		}
	}

	private void SetReferencePath (string? path)
	{
		layer.ReferencePath = path;
		layer.ReferenceMissing = false;
		if (path is not null)
			document.LoadReferencedLayer (layer);
		document.Layers.NotifyLayerTreeChanged ();
		document.Workspace.Invalidate ();
	}
}
