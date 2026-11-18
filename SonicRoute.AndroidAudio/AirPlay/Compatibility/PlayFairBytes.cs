namespace SonicRoute.AirPlay.Compatibility;

internal static class PlayFairBytes
{
    internal static byte[] CopyOfRange(byte[] source, int start, int end) => source.AsSpan(start, end - start).ToArray();

    internal static void Swap(byte[] bytes, int left, int right)
    {
        for (int i = 0; i < 4; i++) (bytes[left + i], bytes[right + i]) = (bytes[right + i], bytes[left + i]);
    }
}
