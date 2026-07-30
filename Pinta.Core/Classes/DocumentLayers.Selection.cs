using System;
using System.ComponentModel;
using Cairo;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	private UserLayer? current_user_layer;
	private GroupLayer? no_selection_layer;

	/// <summary>
	/// Gets the selected user layer, or an internal non-editable layer when no layer is selected.
	/// </summary>
	public UserLayer CurrentUserLayer
		=> current_user_layer ?? (no_selection_layer ??= CreateNoSelectionLayer ());

	public bool HasSelectedLayer => current_user_layer is not null;

	public void SetCurrentUserLayer (UserLayer layer)
	{
		if (!ContainsLayer (layer))
			throw new ArgumentException ("Layer does not belong to this document.", nameof (layer));

		tools.CurrentTool?.DoCommit (document);
		document.ResetSelectionPaths ();
		current_user_layer = layer;
		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);
	}

	public void ClearCurrentUserLayer ()
	{
		if (current_user_layer is null)
			return;

		tools.CurrentTool?.DoCommit (document);
		if (ShowSelectionLayer)
			DestroySelectionLayer ();
		document.ResetSelectionPaths ();
		current_user_layer = null;
		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);
		document.Workspace.Invalidate ();
	}

	private void RaiseLayerPropertyChangedEvent (object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == Layer.HiddenProperty
			&& sender is UserLayer { Hidden: true } hiddenLayer
			&& current_user_layer is not null
			&& ContainsLayer (hiddenLayer, current_user_layer))
			ClearCurrentUserLayer ();

		LayerPropertyChanged?.Invoke (sender, e);
	}

	private GroupLayer CreateNoSelectionLayer ()
		=> new (CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1)) { Hidden = true };
}
