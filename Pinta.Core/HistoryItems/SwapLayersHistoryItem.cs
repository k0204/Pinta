//
// SwapLayersHistoryItem.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//
// Copyright (c) 2010 Jonathan Pobst
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

namespace Pinta.Core;

public sealed class SwapLayersHistoryItem : BaseHistoryItem
{
	private readonly UserLayer layer1;
	private readonly UserLayer layer2;

	public SwapLayersHistoryItem (string icon, string text, UserLayer layer1, UserLayer layer2) : base (icon, text)
	{
		this.layer1 = layer1;
		this.layer2 = layer2;
	}

	public override void Undo ()
	{
		Swap ();
	}

	public override void Redo ()
	{
		Swap ();
	}

	private void Swap ()
	{
		var doc = PintaCore.Workspace.ActiveDocument;
		doc.Layers.SwapLayers (layer1, layer2);
	}
}
