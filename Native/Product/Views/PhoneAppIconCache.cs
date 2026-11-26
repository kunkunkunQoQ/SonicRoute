#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class PhoneAppIconCache(ApplicationHost host, int capacity, Action invalidate)
{
    private readonly Dictionary<string, (byte[] Pixels, int Width, int Height, int Version)> _icons = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _decoding = new(StringComparer.Ordinal);
    private string? _profileId;
    private int _generation;
    private int _version;

    internal void SetProfile(string id)
    {
        if (_profileId == id) return;
        Clear();
        _profileId = id;
    }

    internal void Clear()
    {
        ++_generation;
        _icons.Clear();
        _order.Clear();
        _decoding.Clear();
    }

    internal bool TryGet(AndroidApp app, out byte[] pixels, out int width, out int height, out int version)
    {
        if (app.IconPng is { Length: > 0 } png)
        {
            if (_icons.TryGetValue(app.Package, out var icon))
            {
                (pixels, width, height, version) = icon;
                return pixels.Length > 0;
            }
            if (_decoding.Add(app.Package)) Decode(app.Package, png, _generation);
        }
        pixels = [];
        width = height = version = 0;
        return false;
    }

    private void Decode(string package, byte[] png, int generation)
    {
        _ = Task.Run(() =>
        {
            UiBitmap.TryDecodePng(png, 48, out byte[] pixels, out int width, out int height);
            host.PostContinuation(() =>
            {
                if (generation != _generation) return;
                _decoding.Remove(package);
                if (_icons.Count >= capacity) _icons.Remove(_order.Dequeue());
                _icons[package] = (pixels, width, height, ++_version);
                _order.Enqueue(package);
                if (pixels.Length > 0) invalidate();
            });
        });
    }
}
#endif
