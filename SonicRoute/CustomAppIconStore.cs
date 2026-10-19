using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute.Core;

namespace SonicRoute
{
    internal static class CustomAppIconStore
    {
        internal const int MaxSourceBytes = 8 * 1024 * 1024;
        internal const int MaxDimension = 4096;

        // Called only after a user chooses a file, on a worker. Returns no dispatcher-bound objects.
        internal static byte[] Prepare(string sourcePath)
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > MaxSourceBytes) throw new InvalidDataException();
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (!(decoder is PngBitmapDecoder) && !(decoder is JpegBitmapDecoder) && !(decoder is IconBitmapDecoder))
                throw new InvalidDataException();
            var frame = decoder.Frames.OrderBy(f => Math.Max(f.PixelWidth, f.PixelHeight) >= AppIconFiles.IconSize
                ? Math.Max(f.PixelWidth, f.PixelHeight) : 10000 - Math.Max(f.PixelWidth, f.PixelHeight)).First();
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > MaxDimension || frame.PixelHeight > MaxDimension)
                throw new InvalidDataException();

            BitmapSource image = frame;
            if (!(decoder is IconBitmapDecoder))
            {
                stream.Position = 0;
                var small = new BitmapImage();
                small.BeginInit();
                small.StreamSource = stream;
                small.CacheOption = BitmapCacheOption.OnLoad;
                if (frame.PixelWidth >= frame.PixelHeight) small.DecodePixelWidth = AppIconFiles.IconSize;
                else small.DecodePixelHeight = AppIconFiles.IconSize;
                small.EndInit();
                image = small;
            }
            double scale = Math.Min((double)AppIconFiles.IconSize / image.PixelWidth, (double)AppIconFiles.IconSize / image.PixelHeight);
            double width = image.PixelWidth * scale, height = image.PixelHeight * scale;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var drawing = visual.RenderOpen())
                drawing.DrawImage(image, new Rect((AppIconFiles.IconSize - width) / 2, (AppIconFiles.IconSize - height) / 2, width, height));
            var target = new RenderTargetBitmap(AppIconFiles.IconSize, AppIconFiles.IconSize, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var result = new MemoryStream();
            encoder.Save(result);
            return result.ToArray();
        }

        internal static ImageSource? Load(string fileName)
        {
            try
            {
                using var stream = new MemoryStream(AppIconFiles.ReadPng(fileName), false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
    }
}
