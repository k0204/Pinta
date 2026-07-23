using System.Collections.Generic;

namespace Pinta.Core;

public sealed class PintaDocumentManifest
{
	public string Format { get; set; } = string.Empty;
	public int Version { get; set; }
	public int Width { get; set; }
	public int Height { get; set; }
	public string? SelectedLayerId { get; set; }
	public PintaDocumentSelection Selection { get; set; } = new ();
	public List<PintaDocumentLayerNode> Layers { get; set; } = [];
}

public sealed class PintaDocumentLayerNode
{
	public string Id { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public bool Hidden { get; set; }
	public double Opacity { get; set; }
	public string BlendMode { get; set; } = string.Empty;
	public bool Expanded { get; set; }
	public string Surface { get; set; } = string.Empty;
	public PintaDocumentMatrix Transform { get; set; } = new ();
	public List<PintaDocumentLayerNode> Children { get; set; } = [];
}

public sealed class PintaDocumentSelection
{
	public bool Visible { get; set; }
	public PintaDocumentRectangle HandleBounds { get; set; } = new ();
	public List<List<PintaDocumentPoint>> Polygons { get; set; } = [];
}

public sealed class PintaDocumentRectangle
{
	public double X { get; set; }
	public double Y { get; set; }
	public double Width { get; set; }
	public double Height { get; set; }
}

public sealed class PintaDocumentMatrix
{
	public double Xx { get; set; } = 1;
	public double Yx { get; set; }
	public double Xy { get; set; }
	public double Yy { get; set; } = 1;
	public double X0 { get; set; }
	public double Y0 { get; set; }
}

public sealed class PintaDocumentPoint
{
	public long X { get; set; }
	public long Y { get; set; }
}
