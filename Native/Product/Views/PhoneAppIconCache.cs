#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class PhoneAppIconCache(ApplicationHost host, int capacity, Action invalidate)
{
    private sealed record Icon(byte[] Png, byte[] Pixels, int Width, int Height, int Version);
    private sealed record Metadata(AndroidDevice Device, CancellationToken Attachment, string RequestedName, AndroidApp App);
    private readonly Dictionary<string, Icon> _icons = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, byte[]> _decoding = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Device, string Package), Metadata> _metadata = new();
    private readonly HashSet<(string Device, string Package)> _reading = new();
    private CancellationTokenSource? _visible;
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
        _metadata.Clear();
        _reading.Clear();
        _visible?.Cancel();
        _visible?.Dispose();
        _visible = null;
    }

    internal AndroidApp Resolve(AndroidDevice? device, AndroidApp app)
    {
        if (app.IconPng is { Length: > 0 } || device == null || app.IsDeviceAudio) return app;
        var key = (AndroidDeviceIdentity.Key(device), app.Package);
        if (_metadata.TryGetValue(key, out var found) && ReferenceEquals(found.Device, device) && !found.Attachment.IsCancellationRequested &&
            (found.RequestedName == app.Name || app.Package == app.Name || found.App.Name == app.Name)) return found.App;
        if (_reading.Count >= 4 || _reading.Contains(key)) return app;
        var phone = host.Phone;
        if (!phone.Operations.CanListApps(device)) return app;
        var attachment = phone.Connections.OperationToken(device);
        if (!attachment.IsCancellationRequested)
        {
            _reading.Add(key);
            _visible ??= new();
            ReadMetadata(device, app, key, attachment, _visible.Token, _generation);
        }
        return app;
    }

    private async void ReadMetadata(AndroidDevice device, AndroidApp entry, (string, string) key,
        CancellationToken attachment, CancellationToken visible, int generation)
    {
        var phone = host.Phone;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(attachment, visible);
        AndroidApp resolved = entry;
        try
        {
            var rows = await phone.Operations.AppsAsync(device, read.Token, [entry.Package]).ConfigureAwait(false);
            if (rows.Count == 1 && rows[0].Package == entry.Package) resolved = rows[0];
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { ExceptionDiagnostics.Record("Phone.AppIcon", error); }
        host.PostContinuation(() =>
        {
            if (generation != _generation) return;
            _reading.Remove(key);
            if (!visible.IsCancellationRequested && !attachment.IsCancellationRequested && phone.Connections.IsCurrent(device))
            {
                if (_metadata.Count >= capacity && !_metadata.ContainsKey(key)) _metadata.Remove(_metadata.Keys.First());
                _metadata[key] = new(device, attachment, entry.Name, resolved);
            }
            invalidate();
        });
    }

    internal bool TryGet(AndroidApp app, out byte[] pixels, out int width, out int height, out int version, string? key = null)
    {
        key ??= app.Package;
        if (app.IconPng is { Length: > 0 } png)
        {
            if (_icons.TryGetValue(key, out var icon) && Same(icon.Png, png))
            {
                (pixels, width, height, version) = (icon.Pixels, icon.Width, icon.Height, icon.Version);
                return pixels.Length > 0;
            }
            if (!_decoding.TryGetValue(key, out var queued) || !Same(queued, png))
            {
                _decoding[key] = png;
                Decode(key, png, _generation);
            }
        }
        pixels = [];
        width = height = version = 0;
        return false;
    }

    private static bool Same(byte[] left, byte[] right) => ReferenceEquals(left, right) || left.AsSpan().SequenceEqual(right);

    private void Decode(string package, byte[] png, int generation)
    {
        _ = Task.Run(() =>
        {
            UiBitmap.TryDecodePng(png, 48, out byte[] pixels, out int width, out int height);
            host.PostContinuation(() =>
            {
                if (generation != _generation || !_decoding.TryGetValue(package, out var current) || !ReferenceEquals(current, png)) return;
                _decoding.Remove(package);
                if (!_icons.ContainsKey(package))
                {
                    if (_icons.Count >= capacity) _icons.Remove(_order.Dequeue());
                    _order.Enqueue(package);
                }
                _icons[package] = new(png, pixels, width, height, ++_version);
                if (pixels.Length > 0) invalidate();
            });
        });
    }
}
#endif
