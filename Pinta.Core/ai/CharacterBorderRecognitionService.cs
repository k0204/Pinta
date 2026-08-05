using System;
using Cairo;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

/// <summary>
/// C# client for Baidu's selection-guided intelligent cutout service.
/// </summary>
public sealed class CharacterBorderRecognitionService
{
	private readonly AiJobService jobs;

	public CharacterBorderRecognitionService (AiAuthService auth)
	{
		jobs = new (auth);
	}

	public async Task<CharacterBorderRecognitionResult> RecognizeAsync (
		byte[] sourcePng,
		RectangleI? controlBox,
		CancellationToken cancellationToken = default)
	{
		using JsonDocument json = await jobs.RunBaiduCutoutAsync (
			sourcePng,
			controlBox,
			returnForm: "rgba",
			cancellationToken: cancellationToken);
		if (!TryReadImage (json.RootElement, out byte[]? cutoutPng))
			throw new InvalidOperationException ("Baidu response did not include an intelligent selection image.");

		return new (cutoutPng!, CreateMaskPng (cutoutPng!));
	}

	private static byte[] CreateMaskPng (byte[] cutoutPng)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (cutoutPng);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		using ImageSurface source = CairoExtensions.CreateImageSurface (Format.Argb32, pixbuf.Width, pixbuf.Height);
		using (Context context = new (source))
			context.DrawPixbuf (pixbuf, PointD.Zero);

		using ImageSurface mask = CairoExtensions.CreateImageSurface (Format.Argb32, source.Width, source.Height);
		ReadOnlySpan<ColorBgra> sourcePixels = source.GetReadOnlyPixelData ();
		Span<ColorBgra> maskPixels = mask.GetPixelData ();
		for (int i = 0; i < maskPixels.Length; i++) {
			byte alpha = sourcePixels[i].A;
			maskPixels[i] = ColorBgra.FromBgra (alpha, alpha, alpha, alpha);
		}
		mask.MarkDirty ();
		using GdkPixbuf.Pixbuf maskPixbuf = mask.ToPixbuf ();
		return maskPixbuf.SaveToBuffer ("png");
	}

	private static bool TryReadImage (JsonElement root, out byte[]? image)
	{
		image = null;
		if (!root.TryGetProperty ("result_b64_json", out JsonElement value) ||
			value.GetString () is not string encoded ||
			string.IsNullOrWhiteSpace (encoded))
			return false;

		image = Convert.FromBase64String (encoded);
		return true;
	}
}
