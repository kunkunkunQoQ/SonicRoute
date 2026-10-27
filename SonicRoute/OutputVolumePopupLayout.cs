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
        internal static Rect Arrange(Rect owner, Rect work, Size desired)
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
            int chosen = -1;
            double best = -1;
            for (int i = 0; i < spaces.Length; i++)
            {
                var space = spaces[i];
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
        }
    }
}
