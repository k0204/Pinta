//
// Author:
//       Cameron White <cameronwhite91@gmail.com>
//
// Copyright (c) 2020 Jonathan Pobst
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
using Pinta.Core;

namespace Pinta.Docking;

/// <summary>
/// Content hosted by the dock layout. A dock item has one stable identity and can
/// move between tab groups, split regions, and floating windows.
/// </summary>
[GObject.Subclass<Gtk.Box>]
public sealed partial class DockItem
{
	private string label = string.Empty;

	public string UniqueName { get; private set; } = string.Empty;
	public string IconName { get; private set; } = string.Empty;
	public bool Locked { get; private set; }
	internal DockPlacement DefaultPlacement { get; set; }

	public string Label {
		get => label;
		set {
			if (label == value)
				return;
			label = value;
			LabelChanged?.Invoke (this, EventArgs.Empty);
		}
	}

	public event EventHandler? LabelChanged;

	partial void Initialize ()
	{
		SetOrientation (Gtk.Orientation.Vertical);
		Hexpand = true;
		Vexpand = true;
	}

	public static DockItem New (
		Gtk.Widget child,
		string uniqueName,
		string iconName,
		bool locked = false)
	{
		DockItem item = NewWithProperties ([]);
		item.UniqueName = uniqueName;
		item.IconName = iconName;
		item.Locked = locked;

		child.Hexpand = true;
		child.Vexpand = true;
		child.Halign = Gtk.Align.Fill;
		child.Valign = Gtk.Align.Fill;
		item.Append (child);
		return item;
	}

	public Gtk.Box AddToolBar ()
	{
		Gtk.Box toolbar = GtkExtensions.CreateToolBar ();
		toolbar.Spacing = -4;
		Append (toolbar);
		return toolbar;
	}
}
