using System.Collections.Generic;
using Cairo;

namespace Pinta.Core;

public sealed class GroupLayer : UserLayer
{
        public GroupLayer (ImageSurface surface)
                : base (surface)
        {
        }

        public GroupLayer (
                ImageSurface surface,
                bool hidden,
                double opacity,
                string name)
                : base (surface, hidden, opacity, name)
        {
        }

        internal override IEnumerable<Layer> GetOwnLayersToPaint ()
        {
                yield break;
        }
}
