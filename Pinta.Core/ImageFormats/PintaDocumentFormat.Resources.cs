using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Cairo;
using Path = System.IO.Path;

namespace Pinta.Core;

public sealed partial class PintaDocumentFormat
{
	private readonly record struct PendingResource (string RelativePath, ImageSurface Surface);
	private readonly record struct PendingFile (string RelativePath, string SourcePath);

	private static void WritePendingResources (
		string root,
		string stagingRoot,
		IReadOnlyList<PendingResource> pending,
		ICollection<string> createdResources,
		IProgress<double>? progress)
	{
		for (int index = 0; index < pending.Count; index++) {
			PendingResource resource = pending[index];
			string stagingPath = Path.Combine (stagingRoot, ToSystemPath (resource.RelativePath));
			string finalPath = Path.Combine (root, ToSystemPath (resource.RelativePath));
			Directory.CreateDirectory (Path.GetDirectoryName (stagingPath)!);
			Directory.CreateDirectory (Path.GetDirectoryName (finalPath)!);

			CairoExtensions.SaveToPng (resource.Surface, stagingPath);
			File.Move (stagingPath, finalPath);
			createdResources.Add (resource.RelativePath);
			progress?.Report (pending.Count == 0 ? 0.9 : 0.9 * (index + 1) / pending.Count);
		}
	}

	private static void WritePendingFiles (
		string root,
		string stagingRoot,
		IReadOnlyList<PendingFile> pending,
		ICollection<string> createdResources)
	{
		foreach (PendingFile resource in pending) {
			string stagingPath = Path.Combine (stagingRoot, ToSystemPath (resource.RelativePath));
			string finalPath = Path.Combine (root, ToSystemPath (resource.RelativePath));
			Directory.CreateDirectory (Path.GetDirectoryName (stagingPath)!);
			Directory.CreateDirectory (Path.GetDirectoryName (finalPath)!);
			File.Copy (resource.SourcePath, stagingPath);
			File.Move (stagingPath, finalPath);
			createdResources.Add (resource.RelativePath);
		}
	}

	private static string? GetVideoResource (
		VideoEditingLayer layer,
		string root,
		string? previousPath,
		string saveId,
		ICollection<PendingFile> pending)
	{
		if (string.IsNullOrWhiteSpace (layer.VideoPath))
			return null;

		string sourcePath = Path.GetFullPath (layer.VideoPath);
		if (!File.Exists (sourcePath))
			throw new IOException (Translations.GetString ("The imported video file could not be found."));
		if (previousPath is not null
			&& string.Equals (
				Path.GetFullPath (ResolveManagedResourcePath (root, previousPath)),
				sourcePath,
				OperatingSystem.IsWindows () ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
			return previousPath;

		string extension = Path.GetExtension (sourcePath).ToLowerInvariant ();
		string relativePath = $"{resources_directory}/videos/{layer.DocumentId}/{saveId}{extension}";
		pending.Add (new PendingFile (relativePath, sourcePath));
		return relativePath;
	}

	private static void ApplyVideoPaths (
		IReadOnlyList<UserLayer> layers,
		IReadOnlyList<PintaDocumentLayerNode> nodes,
		string root)
	{
		for (int index = 0; index < layers.Count; index++) {
			if (layers[index] is VideoEditingLayer videoLayer && nodes[index].Video is string video)
				videoLayer.VideoPath = ResolveManagedResourcePath (root, video);
			ApplyVideoPaths (layers[index].Children, nodes[index].Children, root);
		}
	}

	private static (string? Path, string Hash) GetResource (
		ImageSurface surface,
		string root,
		string? previousPath,
		string? previousHash,
		string newPath,
		ICollection<PendingResource> pending)
	{
		string hash = HashSurface (surface);
		if (previousPath is not null
			&& previousHash == hash
			&& File.Exists (Path.Combine (root, ToSystemPath (previousPath))))
			return (previousPath, hash);

		pending.Add (new PendingResource (newPath, surface));
		return (newPath, hash);
	}

	private static string HashSurface (ImageSurface surface)
	{
		surface.Flush ();
		using IncrementalHash hash = IncrementalHash.CreateHash (HashAlgorithmName.SHA256);
		Span<byte> dimensions = stackalloc byte[sizeof (int) * 2];
		BinaryPrimitives.WriteInt32LittleEndian (dimensions, surface.Width);
		BinaryPrimitives.WriteInt32LittleEndian (dimensions[sizeof (int)..], surface.Height);
		hash.AppendData (dimensions);
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();
		for (int y = 0; y < surface.Height; y++) {
			ReadOnlySpan<ColorBgra> row = pixels.Slice (y * surface.Width, surface.Width);
			hash.AppendData (MemoryMarshal.AsBytes (row));
		}

		return Convert.ToHexString (hash.GetHashAndReset ());
	}

	private static string ToSystemPath (string relativePath)
		=> relativePath.Replace ('/', Path.DirectorySeparatorChar);
}
