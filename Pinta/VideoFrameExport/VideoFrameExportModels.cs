using System;

namespace Pinta;

internal sealed record VideoMetadata (
	int Width,
	int Height,
	double FrameRate,
	double Duration,
	int TotalFrames);

internal sealed record VideoFramePreview (
	int SourceIndex,
	TimeSpan Time,
	string SourcePath);

internal sealed class VideoFrameExportException : Exception
{
	public VideoFrameExportException (string message, string? details = null)
		: base (message)
	{
		Details = details ?? message;
	}

	public string Details { get; }
}
