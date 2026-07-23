//
// OraFormat.cs
//
// Author:
//       Maia Kozheva <sikon@ubuntu.com>
//
// Copyright (c) 2010 Maia Kozheva <sikon@ubuntu.com>
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using Cairo;
using GdkPixbuf;

namespace Pinta.Core;

public sealed class OraFormat : IImageImporter, IImageExporter
{
	private const int THUMB_MAX_SIZE = 256;
	private const string pinta_namespace = "https://pinta-project.com/openraster";

	public Document Import (Gio.File file)
	{
		using GioStream stream = new (file.Read (cancellable: null));
		using ZipArchive zipfile = new (stream);

		XmlDocument stackXml = new ();

		ZipArchiveEntry stackXmlEntry = zipfile.GetEntry ("stack.xml") ?? throw new XmlException ("No 'stack.xml' found in OpenRaster file");
		stackXml.Load (stackXmlEntry.Open ());

		// NRT - This makes a lot of assumptions that the file will be perfectly
		// valid that we need to guard against.
		XmlElement imageElement = stackXml.DocumentElement!;

		Size imageSize = new (
			Width: int.Parse (imageElement.GetAttribute ("w")),
			Height: int.Parse (imageElement.GetAttribute ("h"))
		);

		Document newDocument = new (
			PintaCore.Actions,
			PintaCore.Tools,
			PintaCore.Workspace,
			imageSize,
			file,
			"ora");

		XmlElement? stackElement = imageElement["stack"];
		if (stackElement is null)
			throw new XmlException ("No layer stack found in OpenRaster file");

		if (!ImportStack (stackElement, newDocument, zipfile, parent: null))
			throw new XmlException ("No layers found in OpenRaster file");

		return newDocument;
	}

	private static CultureInfo GetFormat ()
		=> CultureInfo.CreateSpecificCulture ("en");

	private static string GetAttribute (XmlElement element, string attribute, string defValue)
	{
		string ret = element.GetAttribute (attribute);
		return string.IsNullOrEmpty (ret) ? defValue : ret;
	}

	private static Size GetThumbDimensions (int width, int height)
	{
		if (width <= THUMB_MAX_SIZE && height <= THUMB_MAX_SIZE)
			return new (width, height);
		else if (width > height)
			return new (THUMB_MAX_SIZE, (int) ((double) height / width * THUMB_MAX_SIZE));
		else
			return new ((int) ((double) width / height * THUMB_MAX_SIZE), THUMB_MAX_SIZE);
	}

	private static bool ImportStack (
		XmlElement stack,
		Document document,
		ZipArchive archive,
		UserLayer? parent)
	{
		bool imported = false;
		int layerNumber = 0;

		foreach (XmlNode node in stack.ChildNodes) {
			if (node is not XmlElement element)
				continue;

			if (element.Name == "layer" && parent is not null && element.GetAttribute ("surface", pinta_namespace) == "true") {
				LoadLayerSurface (element, parent, archive);
				continue;
			}

			if (element.Name == "layer") {
				UserLayer layer = CreateLayer (element, document, layerNumber++);
				InsertImportedLayer (document, layer, parent);
				LoadLayerSurface (element, layer, archive);
				imported = true;
			} else if (element.Name == "stack") {
				UserLayer layer = CreateLayer (element, document, layerNumber++);
				InsertImportedLayer (document, layer, parent);
				ImportStack (element, document, archive, layer);
				imported = true;
			}
		}

		return imported;
	}

	private static UserLayer CreateLayer (XmlElement element, Document document, int number)
	{
		UserLayer layer = document.Layers.CreateLayer (GetAttribute (element, "name", $"Layer {number}"));
		layer.Hidden = GetAttribute (element, "visibility", "visible") == "hidden";
		layer.Opacity = double.Parse (GetAttribute (element, "opacity", "1"), GetFormat ());
		layer.BlendMode = StandardToBlendMode (GetAttribute (element, "composite-op", "svg:src-over"));
		return layer;
	}

	private static void InsertImportedLayer (Document document, UserLayer layer, UserLayer? parent)
		=> document.Layers.Insert (layer, new LayerPosition (parent, 0));

	private static void LoadLayerSurface (XmlElement element, UserLayer layer, ZipArchive archive)
	{
		PointI position = new (
			X: int.Parse (GetAttribute (element, "x", "0")),
			Y: int.Parse (GetAttribute (element, "y", "0")));
		string name = layer.Name;
		string? temporaryFile = null;

		try {
			ZipArchiveEntry entry = archive.GetEntry (element.GetAttribute ("src")) ?? throw new XmlException ("Missing layer in OpenRaster file");
			temporaryFile = System.IO.Path.GetTempFileName ();
			using (Stream source = entry.Open ())
			using (Stream destination = File.Open (temporaryFile, FileMode.OpenOrCreate))
				source.CopyTo (destination);

			using Pixbuf pixbuf = Pixbuf.NewFromFile (temporaryFile)!;
			using Context context = new (layer.Surface);
			context.DrawPixbuf (pixbuf, (PointD) position);
		} catch {
			string details = Translations.GetString ("Could not import layer \"{0}\" from {1}", name, archive);
			PintaCore.Chrome.ShowMessageDialog (PintaCore.Chrome.MainWindow, Translations.GetString ("Error"), details);
		} finally {
			if (temporaryFile is not null) {
				try {
					File.Delete (temporaryFile);
				} catch { }
			}
		}
	}

	private static byte[] GetLayerXmlData (IReadOnlyList<UserLayer> rootLayers, IReadOnlyList<UserLayer> allLayers)
	{
		using MemoryStream ms = new ();
		using XmlTextWriter writer = new (ms, System.Text.Encoding.UTF8) {
			Formatting = Formatting.Indented,
		};

		writer.WriteStartElement ("image");
		writer.WriteAttributeString ("w", rootLayers[0].Surface.Width.ToString ());
		writer.WriteAttributeString ("h", rootLayers[0].Surface.Height.ToString ());
		writer.WriteAttributeString ("version", "0.0.5"); // Current version of the spec.

		writer.WriteStartElement ("stack");

		Dictionary<UserLayer, int> indexes = [];
		for (int i = 0; i < allLayers.Count; i++)
			indexes.Add (allLayers[i], i);

		for (int i = rootLayers.Count - 1; i >= 0; i--)
			WriteLayerNode (writer, rootLayers[i], indexes);

		writer.WriteEndElement (); // stack
		writer.WriteEndElement (); // image

		writer.Close ();

		return ms.ToArray ();
	}

	private static void WriteLayerNode (XmlWriter writer, UserLayer layer, IReadOnlyDictionary<UserLayer, int> indexes)
	{
		if (!layer.HasChildren) {
			WriteLayerElement (writer, layer, indexes[layer], isContainerSurface: false);
			return;
		}

		writer.WriteStartElement ("stack");
		WriteLayerAttributes (writer, layer);

		for (int i = layer.Children.Count - 1; i >= 0; i--)
			WriteLayerNode (writer, layer.Children[i], indexes);

		WriteLayerElement (writer, layer, indexes[layer], isContainerSurface: true);
		writer.WriteEndElement ();
	}

	private static void WriteLayerElement (XmlWriter writer, UserLayer layer, int index, bool isContainerSurface)
	{
		writer.WriteStartElement ("layer");
		if (isContainerSurface)
			writer.WriteAttributeString ("pinta", "surface", pinta_namespace, "true");
		else
			WriteLayerAttributes (writer, layer);

		writer.WriteAttributeString ("src", $"data/layer{index}.png");
		writer.WriteEndElement ();
	}

	private static void WriteLayerAttributes (XmlWriter writer, UserLayer layer)
	{
		writer.WriteAttributeString ("opacity", string.Format (GetFormat (), "{0:0.00}", layer.Opacity));
		writer.WriteAttributeString ("name", layer.Name);
		writer.WriteAttributeString ("composite-op", BlendModeToStandard (layer.BlendMode));
		if (layer.Hidden)
			writer.WriteAttributeString ("visibility", "hidden");
	}

	public void Export (Document document, Gio.File file, Gtk.Window parent)
	{
		using GioStream file_stream = new (file.Replace ());
		using ZipArchive archive = new (file_stream, ZipArchiveMode.Create);
		using Pixbuf flattened = document.GetFlattenedImage ().ToPixbuf ();
		AddMimeEntry (archive);
		AddLayerEntries (archive, document);
		AddStackEntry (archive, document);
		AddMergedImage (archive, flattened);
		AddThumbnail (archive, flattened);
	}

	private static void AddMimeEntry (ZipArchive archive)
	{
		byte[] mimeBytes = System.Text.Encoding.ASCII.GetBytes ("image/openraster");
		ZipArchiveEntry mimeEntry = archive.CreateEntry ("mimetype", CompressionLevel.NoCompression);
		using Stream mimeStream = mimeEntry.Open ();
		mimeStream.Write (mimeBytes, 0, mimeBytes.Length);
	}

	private static void AddLayerEntries (ZipArchive archive, Document document)
	{
		for (int i = 0; i < document.Layers.AllLayers.Count; i++) {
			using Pixbuf pb = document.Layers.AllLayers[i].Surface.ToPixbuf ();
			byte[] buf = pb.SaveToBuffer ("png");
			ZipArchiveEntry layerEntry = archive.CreateEntry ($"data/layer{i}.png");
			using Stream layerStream = layerEntry.Open ();
			layerStream.Write (buf, 0, buf.Length);
		}
	}

	private static void AddStackEntry (ZipArchive archive, Document document)
	{
		byte[] userLayerBytes = GetLayerXmlData (document.Layers.RootLayers, document.Layers.AllLayers);
		ZipArchiveEntry stackEntry = archive.CreateEntry ("stack.xml");
		using Stream stackStream = stackEntry.Open ();
		stackStream.Write (userLayerBytes, 0, userLayerBytes.Length);
	}

	private static void AddMergedImage (ZipArchive archive, Pixbuf flattened)
	{
		byte[] mergedImageBytes = flattened.SaveToBuffer ("png");
		ZipArchiveEntry mergedImageEntry = archive.CreateEntry ("mergedimage.png");
		using Stream mergedImageStream = mergedImageEntry.Open ();
		mergedImageStream.Write (mergedImageBytes, 0, mergedImageBytes.Length);
	}

	private static void AddThumbnail (ZipArchive archive, Pixbuf flattened)
	{
		Size newSize = GetThumbDimensions (flattened.Width, flattened.Height);
		using Pixbuf thumb = flattened.ScaleSimple (newSize.Width, newSize.Height, InterpType.Bilinear)!; // Creates new Pixbuf
		byte[] thumbnailBytes = thumb.SaveToBuffer ("png");
		ZipArchiveEntry thumbnailEntry = archive.CreateEntry ("Thumbnails/thumbnail.png");
		using Stream thumbnailStream = thumbnailEntry.Open ();
		thumbnailStream.Write (thumbnailBytes, 0, thumbnailBytes.Length);
	}

	private static string BlendModeToStandard (BlendMode mode)
		=> mode switch {
			BlendMode.Multiply => "svg:multiply",
			BlendMode.ColorBurn => "svg:color-burn",
			BlendMode.ColorDodge => "svg:color-dodge",
			BlendMode.Overlay => "svg:overlay",
			BlendMode.Difference => "svg:difference",
			BlendMode.Lighten => "svg:lighten",
			BlendMode.Darken => "svg:darken",
			BlendMode.Screen => "svg:screen",
			BlendMode.Xor => "svg:xor",
			BlendMode.HardLight => "svg:hard-light",
			BlendMode.SoftLight => "svg:soft-light",
			BlendMode.Color => "svg:color",
			BlendMode.Luminosity => "svg:luminosity",
			BlendMode.Hue => "svg:hue",
			BlendMode.Saturation => "svg:saturation",
			_ => "svg:src-over",
		};

	private static BlendMode StandardToBlendMode (string mode)
	{
		switch (mode) {
			case "svg:src-over":
				return BlendMode.Normal;
			case "svg:multiply":
				return BlendMode.Multiply;
			case "svg:color-burn":
				return BlendMode.ColorBurn;
			case "svg:color-dodge":
				return BlendMode.ColorDodge;
			case "svg:overlay":
				return BlendMode.Overlay;
			case "svg:difference":
				return BlendMode.Difference;
			case "svg:lighten":
				return BlendMode.Lighten;
			case "svg:darken":
				return BlendMode.Darken;
			case "svg:screen":
				return BlendMode.Screen;
			case "svg:xor":
				return BlendMode.Xor;
			case "svg:hard-light":
				return BlendMode.HardLight;
			case "svg:soft-light":
				return BlendMode.SoftLight;
			case "svg:color":
				return BlendMode.Color;
			case "svg:luminosity":
				return BlendMode.Luminosity;
			case "svg:hue":
				return BlendMode.Hue;
			case "svg:saturation":
				return BlendMode.Saturation;
			default:
				Console.WriteLine ("Unrecognized composite-op: {0}, using Normal.", mode);
				return BlendMode.Normal;
		}
	}
}
