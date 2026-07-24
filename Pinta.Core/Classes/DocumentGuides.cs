using System;
using System.Collections.Generic;

namespace Pinta.Core;

public enum GuideOrientation
{
        Horizontal,
        Vertical,
}

public readonly record struct DocumentGuide (GuideOrientation Orientation, double Position);

public sealed class DocumentGuides
{
        private readonly Document document;
        private readonly List<DocumentGuide> guides = [];

        internal DocumentGuides (Document document)
        {
                this.document = document;
        }

        public IReadOnlyList<DocumentGuide> Items => guides;

        public int Count => guides.Count;

        public int AddHorizontal (double position) => AddGuide (GuideOrientation.Horizontal, position);

        public int AddVertical (double position) => AddGuide (GuideOrientation.Vertical, position);

        public void Clear ()
        {
                if (guides.Count == 0)
                        return;

                guides.Clear ();
                Changed?.Invoke (this, EventArgs.Empty);
        }

        public void ReplaceAll (IEnumerable<DocumentGuide> items)
        {
                List<DocumentGuide> updated = [];

                foreach (DocumentGuide guide in items)
                        updated.Add (guide with { Position = ClampPosition (guide.Orientation, guide.Position) });

                if (guides.Count == updated.Count) {
                        bool same = true;

                        for (int i = 0; i < guides.Count; i++) {
                                if (guides[i] == updated[i])
                                        continue;

                                same = false;
                                break;
                        }

                        if (same)
                                return;
                }

                guides.Clear ();
                guides.AddRange (updated);
                Changed?.Invoke (this, EventArgs.Empty);
        }

        public void ClampAllToImageBounds ()
        {
                bool changed = false;

                for (int i = 0; i < guides.Count; i++) {
                        DocumentGuide guide = guides[i];
                        DocumentGuide clamped = guide with { Position = ClampPosition (guide.Orientation, guide.Position) };

                        if (clamped == guide)
                                continue;

                        guides[i] = clamped;
                        changed = true;
                }

                if (changed)
                        Changed?.Invoke (this, EventArgs.Empty);
        }

        public bool RemoveAt (int index)
        {
                if (index < 0 || index >= guides.Count)
                        return false;

                guides.RemoveAt (index);
                Changed?.Invoke (this, EventArgs.Empty);
                return true;
        }

        public bool UpdateAt (int index, double position)
        {
                if (index < 0 || index >= guides.Count)
                        return false;

                DocumentGuide guide = guides[index];
                DocumentGuide updated = guide with { Position = ClampPosition (guide.Orientation, position) };
                if (updated == guide)
                        return false;

                guides[index] = updated;
                Changed?.Invoke (this, EventArgs.Empty);
                return true;
        }

        private int AddGuide (GuideOrientation orientation, double position)
        {
                DocumentGuide guide = new (orientation, ClampPosition (orientation, position));
                guides.Add (guide);
                Changed?.Invoke (this, EventArgs.Empty);
                return guides.Count - 1;
        }

        private double ClampPosition (GuideOrientation orientation, double position)
        {
                double max = orientation == GuideOrientation.Horizontal
                        ? document.ImageSize.Height
                        : document.ImageSize.Width;

                return Math.Clamp (position, 0, max);
        }

        public event EventHandler? Changed;
}
