using Cairo;
using NUnit.Framework;

namespace Pinta.Core.Tests;

[TestFixture]
internal sealed class UserLayerTreeTests
{
	[Test]
	public void InsertChild_RejectsCycles ()
	{
		using ImageSurface parentSurface = CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1);
		using ImageSurface childSurface = CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1);
		UserLayer parent = new (parentSurface);
		UserLayer child = new (childSurface);

		parent.InsertChild (0, child);

		Assert.That (() => child.InsertChild (0, parent), Throws.InvalidOperationException);
		Assert.That (parent.Children, Is.EqualTo (new[] { child }));
		Assert.That (child.Parent, Is.SameAs (parent));
	}
}
