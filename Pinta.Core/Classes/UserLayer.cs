//
// UserLayer.cs
//
// Author:
//       Andrew Davis <andrew.3.1415@gmail.com>
//
// Copyright (c) 2013 Andrew Davis, GSoC 2012 and GSoC 2013
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
using System.Collections.ObjectModel;
using Cairo;

namespace Pinta.Core;

/// <summary>
/// A UserLayer is a Layer that the user interacts with directly. Each UserLayer contains special layers
/// and some other special variables that allow for re-editability of various things.
/// </summary>
public class UserLayer : Layer
{
	private readonly List<UserLayer> children = [];

	//Special layers to be drawn on to keep things editable by drawing them separately from the UserLayers.
	internal Collection<ReEditableLayer> ReEditableLayers { get; } = [];
	public ReEditableLayer TextLayer { get; }

	//Call the base class constructor and setup the engines.
	public UserLayer (ImageSurface surface)
		: this (surface, false, 1f, "")
	{ }

	//Call the base class constructor and setup the engines.
	public UserLayer (
		ImageSurface surface,
		bool hidden,
		double opacity,
		string name
	)
		: base (surface, hidden, opacity, name)
	{
		TextEngine = new TextEngine ();
		TextLayer = new ReEditableLayer (this);
	}

	//Stores most of the editable text's data, including the text itself.
	public TextEngine TextEngine { get; internal set; }

	public UserLayer? Parent { get; private set; }
	public IReadOnlyList<UserLayer> Children => children;
	public Dictionary<string, string> Metadata { get; } = [];
	public bool HasChildren => children.Count > 0;
	internal List<UserLayer> MutableChildren => children;
	internal string? DocumentId { get; set; }
	public bool Expanded { get; set; } = true;

	/// <summary>
	/// Path to an image below the document resource root. A layer with a reference path
	/// is rendered from the loaded source image but is not written into a Pinta project.
	/// </summary>
	public string? ReferencePath { get; internal set; }
	public bool ReferenceMissing { get; internal set; }
	public Size ReferenceSize { get; internal set; }
	public bool IsReference => ReferencePath is not null;
	public bool IsEditable => this is not GroupLayer && !IsReference;

	//Rectangular boundary surrounding the editable text.
	public RectangleI TextBounds { get; set; } = RectangleI.Zero;
	public RectangleI PreviousTextBounds { get; set; } = RectangleI.Zero;

	public override void ApplyTransform (
		Matrix xform,
		Size old_size,
		Size new_size)
	{
		ApplyTransform (xform, old_size, new_size, null);
	}

	public override void ApplyTransform (
		Matrix xform,
		Size old_size,
		Size new_size,
		ResamplingMode? resamplingMode)
	{
		base.ApplyTransform (xform, old_size, new_size, resamplingMode);

		foreach (ReEditableLayer rel in ReEditableLayers) {
			if (rel.IsLayerSetup)
				rel.Layer.ApplyTransform (xform, old_size, new_size, resamplingMode);
		}
	}

	public void Rotate (
		DegreesAngle angle,
		Size old_size,
		Size new_size)
	{
		RadiansAngle radians = angle.ToRadians ();

		Matrix xform = CairoExtensions.CreateIdentityMatrix ();
		xform.Translate (new_size.Width / 2.0, new_size.Height / 2.0);
		xform.Rotate (radians.Radians);
		xform.Translate (-old_size.Width / 2.0, -old_size.Height / 2.0);

		ApplyTransform (xform, old_size, new_size);
	}

	public override void Crop (RectangleI rect, Path? selection)
	{
		base.Crop (rect, selection);

		foreach (ReEditableLayer rel in ReEditableLayers)
			if (rel.IsLayerSetup)
				rel.Layer.Crop (rect, selection);
	}

	public override void ResizeCanvas (Size newSize, Anchor anchor)
	{
		base.ResizeCanvas (newSize, anchor);

		foreach (ReEditableLayer rel in ReEditableLayers)
			if (rel.IsLayerSetup)
				rel.Layer.ResizeCanvas (newSize, anchor);
	}

	public override void Resize (Size newSize, ResamplingMode resamplingMode)
	{
		base.Resize (newSize, resamplingMode);

		foreach (ReEditableLayer rel in ReEditableLayers)
			if (rel.IsLayerSetup)
				rel.Layer.Resize (newSize, resamplingMode);
	}

	/// <summary>
	/// Returns a list of the layers to paint for this layer and its visible child layers.
	/// This includes the primary layer and any active re-editable layers.
	/// </summary>
	public IEnumerable<Layer> GetLayersToPaint ()
	{
		foreach (Layer layer in GetOwnLayersToPaint ())
			yield return layer;

		foreach (UserLayer child in children) {
			if (child.Hidden)
				continue;

			foreach (Layer layer in child.GetLayersToPaint ())
				yield return layer;
		}
	}

        internal virtual IEnumerable<Layer> GetOwnLayersToPaint ()
	{
		yield return this;

		foreach (ReEditableLayer rel in ReEditableLayers) {
			if (rel.IsLayerSetup)
				yield return rel.Layer;
		}
	}

	internal int ChildIndexOf (UserLayer layer)
		=> children.IndexOf (layer);

	internal void InsertChild (int index, UserLayer layer)
	{
		if (layer == this || layer.IsAncestorOf (this))
			throw new InvalidOperationException ("Cannot add a layer as a child of itself or one of its descendants.");

		layer.Parent?.RemoveChild (layer);
		children.Insert (index, layer);
		layer.Parent = this;
	}

	internal void RemoveChild (UserLayer layer)
	{
		if (children.Remove (layer))
			layer.Parent = null;
	}

	internal void DetachFromParent ()
	{
		Parent?.RemoveChild (this);
	}

	internal IEnumerable<UserLayer> GetSelfAndDescendants ()
	{
		yield return this;

		foreach (UserLayer child in children)
			foreach (UserLayer descendant in child.GetSelfAndDescendants ())
				yield return descendant;
	}

	private bool IsAncestorOf (UserLayer layer)
	{
		for (UserLayer? parent = layer.Parent; parent is not null; parent = parent.Parent)
			if (parent == this)
				return true;

		return false;
	}
}
