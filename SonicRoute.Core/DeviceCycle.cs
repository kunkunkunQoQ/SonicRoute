namespace SonicRoute.Core
{
    public static class DeviceCycle
    {
        public static int Index(int currentIndex, int count, bool previous, int steps = 1)
        {
            if (count <= 0) return -1;
            if (currentIndex < 0 || currentIndex >= count) currentIndex = previous ? 0 : count - 1;
            int offset = System.Math.Max(1, steps) % count;
            return previous ? (currentIndex + count - offset) % count : (currentIndex + offset) % count;
        }
    }
}
