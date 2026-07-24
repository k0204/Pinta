using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pinta.PsdAddin;

internal sealed class PsdImportManifest
{
	[JsonPropertyName ("width")]
	public int Width { get; init; }

	[JsonPropertyName ("height")]
	public int Height { get; init; }

	[JsonPropertyName ("selectedLayerId")]
	public string? SelectedLayerId { get; init; }

	[JsonPropertyName ("layers")]
	public IReadOnlyList<PsdImportLayerNode> Layers { get; init; } = [];
}

internal sealed class PsdImportLayerNode
{
	[JsonPropertyName ("id")]
	public string Id { get; init; } = string.Empty;

	[JsonPropertyName ("name")]
	public string Name { get; init; } = string.Empty;

	[JsonPropertyName ("hidden")]
	public bool Hidden { get; init; }

	[JsonPropertyName ("opacity")]
	public double Opacity { get; init; } = 1.0;

	[JsonPropertyName ("blendMode")]
	public string BlendMode { get; init; } = "normal";

	[JsonPropertyName ("kind")]
	public string Kind { get; init; } = string.Empty;

	[JsonPropertyName ("surface")]
	public string Surface { get; init; } = string.Empty;

	[JsonPropertyName ("children")]
	public IReadOnlyList<PsdImportLayerNode> Children { get; init; } = [];
}
