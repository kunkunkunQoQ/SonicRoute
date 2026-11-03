using System;
using System.Windows;
using SonicRoute.Core.Compat;
using Size = System.Windows.Size;

namespace SonicRoute
{
    internal static class OutputVolumePopupLayout
    {
        // Prefer above. In the top-left corner the right-hand candidate is next.
        // Constrain the extra panel, leaving the owner and its saved position intact.
        internal static Rect Arrange(Rect owner, Rect work, Size desired, Rect? occupied = null)
        {
            const double gap = 6;
            double width = Math.Min(desired.Width, work.Width);
            double height = Math.Min(desired.Height, work.Height);
            var spaces = new[]
            {
                new Rect(work.Left, work.Top, work.Width, Math.Max(0, owner.Top - gap - work.Top)),
                new Rect(Math.Min(work.Right, owner.Right + gap), work.Top, Math.Max(0, work.Right - owner.Right - gap), work.Height),
                new Rect(work.Left, work.Top, Math.Max(0, owner.Left - gap - work.Left), work.Height),
                new Rect(work.Left, Math.Min(work.Bottom, owner.Bottom + gap), work.Width, Math.Max(0, work.Bottom - owner.Bottom - gap))
            };
            int[] order = occupied.HasValue ? new[] { 3, 0, 1, 2 } : new[] { 0, 1, 2, 3 };
            int chosen = -1;
            double best = -1;
            foreach (int i in order)
            {
                var space = spaces[i];
                double candidateHeight = Math.Min(height, space.Height);
                var candidate = Candidate(i, space, candidateHeight);
                if (occupied.HasValue && candidate.IntersectsWith(occupied.Value)) continue;
                if (space.Width >= width && space.Height >= height) { chosen = i; break; }
                // Prefer a full-width readable list; its height can scroll.
                double area = space.Width >= width ? width * Math.Min(height, space.Height) : 0;
                if (area > best) { best = area; chosen = i; }
            }
            if (chosen < 0 || best == 0 && (spaces[chosen].Width < width || spaces[chosen].Height == 0))
                return new Rect(work.Left, work.Top, width, height);
            var available = spaces[chosen];
            height = Math.Min(height, available.Height);
            double x = chosen == 1 ? available.Left : chosen == 2 ? available.Right - width : owner.Left;
            double y = chosen == 0 ? available.Bottom - height : chosen == 3 ? available.Top : owner.Top;
            x = MathEx.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = MathEx.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
            return new Rect(x, y, width, height);

            Rect Candidate(int side, Rect available, double candidateHeight)
            {
                double cx = side == 1 ? available.Left : side == 2 ? available.Right - width : owner.Left;
                double cy = side == 0 ? available.Bottom - candidateHeight : side == 3 ? available.Top : owner.Top;
                return new Rect(MathEx.Clamp(cx, work.Left, Math.Max(work.Left, work.Right - width)),
                    MathEx.Clamp(cy, work.Top, Math.Max(work.Top, work.Bottom - candidateHeight)), width, candidateHeight);
            }
        }
    }
}
