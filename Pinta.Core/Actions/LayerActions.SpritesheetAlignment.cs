using System;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private const int sprite_alignment_tolerance = 2;
	private const int sprite_background_distance = 48;

	private static void AlignSpriteFrame (Cairo.ImageSurface surface, string backgroundId, bool alignBaseline)
	{
		if (!TryGetSpriteBackground (backgroundId, out ColorBgra background)
			|| !TryGetSpriteBounds (surface, background, out RectangleI bounds))
			return;

		int shiftX = (surface.Width - bounds.Width) / 2 - bounds.X;
		int shiftY = alignBaseline ? surface.Height - 2 - bounds.Bottom : 0;
		if (Math.Abs (shiftX) <= sprite_alignment_tolerance)
			shiftX = 0;
		if (Math.Abs (shiftY) <= sprite_alignment_tolerance)
			shiftY = 0;
		if (shiftX == 0 && shiftY == 0)
			return;

		using Cairo.ImageSurface source = surface.Clone ();
		using Cairo.Context context = new (surface);
		context.Operator = Cairo.Operator.Source;
		context.SetSourceColor (background.ToCairoColor ());
		context.Paint ();
		context.Operator = Cairo.Operator.Over;
		context.SetSourceSurface (source, shiftX, shiftY);
		context.Paint ();
		surface.MarkDirty ();
	}

	private static bool TryGetSpriteBounds (
		Cairo.ImageSurface surface,
		ColorBgra background,
		out RectangleI bounds)
	{
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();
		int minX = surface.Width;
		int minY = surface.Height;
		int maxX = -1;
		int maxY = -1;
		for (int y = 0; y < surface.Height; y++) {
			for (int x = 0; x < surface.Width; x++) {
				if (!IsSpriteForeground (pixels[y * surface.Width + x], background))
					continue;
				minX = Math.Min (minX, x);
				minY = Math.Min (minY, y);
				maxX = Math.Max (maxX, x);
				maxY = Math.Max (maxY, y);
			}
		}

		bounds = maxX < 0 ? default : new RectangleI (minX, minY, maxX - minX + 1, maxY - minY + 1);
		return maxX >= 0 && bounds.Width < surface.Width && bounds.Height < surface.Height;
	}

	private static bool IsSpriteForeground (ColorBgra color, ColorBgra background)
		=> color.A > 16
		&& Math.Abs (color.R - background.R)
			+ Math.Abs (color.G - background.G)
			+ Math.Abs (color.B - background.B) > sprite_background_distance;

	private static bool TryGetSpriteBackground (string id, out ColorBgra background)
	{
		background = id switch {
			"white" => ColorBgra.FromBgr (255, 255, 255),
			"magenta" => ColorBgra.FromBgr (255, 0, 255),
			"green" => ColorBgra.FromBgr (0, 255, 0),
			_ => default,
		};
		return id is "white" or "magenta" or "green";
	}
}
