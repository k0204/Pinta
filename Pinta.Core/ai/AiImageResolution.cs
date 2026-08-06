using System;
using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core.AI;

public sealed record AiImageResolutionPlan (
	Size TargetSize,
	Size? LowerSize,
	Size? UpperSize)
{
	public bool RequiresChoice
		=> LowerSize is Size lower
			&& UpperSize is Size upper
			&& lower != upper
			&& TargetSize != lower
			&& TargetSize != upper;
}

public static class AiImageResolutionPlanner
{
	public static AiImageResolutionPlan Create (
		string imageService,
		string provider,
		Size targetSize)
	{
		List<Size> candidates = [.. BackgroundCutoutService
			.GetImageGenerationSizes (imageService, provider)
			.Distinct ()];
		if (imageService == AiRequestSettings.GptImageService) {
			candidates.RemoveAll (size => BackgroundCutoutService.GetGptImageSizeError (size) is not null);
			AddGptBoundaryCandidates (candidates, targetSize);
		}

		return new (
			targetSize,
			FindClosest (candidates.Where (size => size.Width <= targetSize.Width && size.Height <= targetSize.Height), targetSize),
			FindClosest (candidates.Where (size => size.Width >= targetSize.Width && size.Height >= targetSize.Height), targetSize));
	}

	private static void AddGptBoundaryCandidates (List<Size> candidates, Size targetSize)
	{
		const int multiple = 16;
		int lowerWidth = targetSize.Width / multiple * multiple;
		int lowerHeight = targetSize.Height / multiple * multiple;
		int upperWidth = checked ((targetSize.Width + multiple - 1) / multiple * multiple);
		int upperHeight = checked ((targetSize.Height + multiple - 1) / multiple * multiple);
		foreach (Size candidate in new[] {
			new Size (lowerWidth, lowerHeight),
			new Size (upperWidth, upperHeight),
		})
			if (candidate.Width > 0 && candidate.Height > 0 &&
				BackgroundCutoutService.GetGptImageSizeError (candidate) is null)
				candidates.Add (candidate);
	}

	private static Size? FindClosest (IEnumerable<Size> candidates, Size targetSize)
	{
		Size[] ordered = [.. candidates.OrderBy (size => GetDistance (size, targetSize))];
		return ordered.Length == 0 ? null : ordered[0];
	}

	private static double GetDistance (Size size, Size targetSize)
	{
		double width = size.Width / (double) targetSize.Width;
		double height = size.Height / (double) targetSize.Height;
		double ratio = Math.Abs (
			Math.Log ((size.Width / (double) size.Height) / (targetSize.Width / (double) targetSize.Height)));
		return Math.Pow (width - 1, 2) + Math.Pow (height - 1, 2) + ratio * ratio;
	}
}
