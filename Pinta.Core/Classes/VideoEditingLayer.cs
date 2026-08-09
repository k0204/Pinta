using System.Collections.Generic;
using Cairo;

namespace Pinta.Core;

/// <summary>
/// Root container for video editing layers and their frame data.
/// </summary>
public sealed class VideoEditingLayer : GroupLayer
{
	internal const string VideoPathMetadataKey = "video.path";
	internal const string SelectedFramesMetadataKey = "video.selected-frames";

	internal VideoEditingLayer (ImageSurface surface, string name)
		: base (surface)
	{
		Name = name;
	}

	public string? VideoPath {
		get => Metadata.TryGetValue (VideoPathMetadataKey, out string? path) ? path : null;
		set {
			if (string.IsNullOrWhiteSpace (value))
				Metadata.Remove (VideoPathMetadataKey);
			else
				Metadata[VideoPathMetadataKey] = value;
		}
	}

	public string? SelectedFrames {
		get => Metadata.TryGetValue (SelectedFramesMetadataKey, out string? value) ? value : null;
		set {
			if (value is null)
				Metadata.Remove (SelectedFramesMetadataKey);
			else
				Metadata[SelectedFramesMetadataKey] = value;
		}
	}
}
