using SonicRoute.Native.Graphics;

namespace SonicRoute.Native.Ui;

internal static class UiBitmap
{
    internal static bool TryDecodePng(byte[] png, int size, out byte[] pixels, out int width, out int height)
    {
        if (!WicIconLoader.TryDecodePngToBgra32(png, size, size, out pixels, out width, out height)) return false;
        Premultiply(pixels);
        return true;
    }

    internal static void Premultiply(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 0;
                continue;
            }
            pixels[i] = (byte)(pixels[i] * a / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
        }
    }
}
