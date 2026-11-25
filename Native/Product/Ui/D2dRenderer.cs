using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SonicRoute.Native.Ui;

internal enum UiTextAlign { Leading = 0, Trailing = 1, Center = 2 }
internal enum UiVertAlign { Top = 0, Bottom = 1, Center = 2 }
internal enum UiWrapMode { NoWrap = 1, Wrap = 0 }

internal readonly record struct UiTextSize(float Width, float Height, int LineCount);

internal sealed unsafe class D2dSharedContext : IDisposable
{
    public static D2dSharedContext Instance { get; } = new();

    private static readonly Guid IidFactory = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static readonly Guid IidWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    private nint _d2d;
    private nint _write;
    private nint _roundStroke;
    private nint _flatStroke;
    private readonly Dictionary<UiIcon, nint> _iconGeometries = new();
    private readonly Dictionary<TextFormatKey, nint> _formats = new();
    private readonly Dictionary<nint, nint> _trimSigns = new();
    private readonly Queue<TextFormatKey> _formatOrder = new();
    private const int MaxFormats = 64;

    private D2dSharedContext() { }

    public nint D2dFactory { get { EnsureInit(); return _d2d; } }
    public nint WriteFactory { get { EnsureInit(); return _write; } }
    public nint RoundStrokeStyle { get { EnsureInit(); return _roundStroke; } }
    public nint FlatStrokeStyle { get { EnsureInit(); return _flatStroke; } }

    public void EnsureInit()
    {
        if (_d2d != 0) return;
        Guid fId = IidFactory;
        Check(D2D1CreateFactory(0, ref fId, 0, out _d2d), "D2D1CreateFactory");
        Guid wId = IidWriteFactory;
        Check(DWriteCreateFactory(0, ref wId, out _write), "DWriteCreateFactory");

        var roundProps = new D2dStrokeStyleProps
        {
            StartCap = 2, // Round
            EndCap = 2,
            DashCap = 2,
            LineJoin = 2, // Round
            MiterLimit = 10f,
            DashStyle = 0, // Solid
            DashOffset = 0f,
        };
        nint rs = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, D2dStrokeStyleProps*, float*, uint, nint*, int>)Slot(_d2d, 11))(_d2d, &roundProps, null, 0, &rs), "CreateStrokeStyle(round)");
        _roundStroke = rs;

        var flatProps = new D2dStrokeStyleProps
        {
            StartCap = 0,
            EndCap = 0,
            DashCap = 0,
            LineJoin = 0,
            MiterLimit = 10f,
            DashStyle = 0,
            DashOffset = 0f,
        };
        nint fs = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, D2dStrokeStyleProps*, float*, uint, nint*, int>)Slot(_d2d, 11))(_d2d, &flatProps, null, 0, &fs), "CreateStrokeStyle(flat)");
        _flatStroke = fs;
    }

    public nint GetTextFormat(
        string fontFamily,
        float fontSize,
        int fontWeight,
        UiTextAlign hAlign,
        UiVertAlign vAlign,
        bool wrap,
        bool ellipsis)
    {
        EnsureInit();
        float roundedSize = MathF.Round(fontSize * 4f) * 0.25f;
        string language = L10n.CurrentLanguage;
        // External translation identifiers can be arbitrary file names.
        string locale = language is "zh-CN" or "zh-TW" or "en-US" or "ja-JP" or "ko-KR" or
            "de-DE" or "fr-FR" or "es-ES" or "ru-RU" ? language : "zh-CN";
        var key = new TextFormatKey(fontFamily, locale, roundedSize, fontWeight, hAlign, vAlign, wrap, ellipsis);
        if (_formats.TryGetValue(key, out nint fmt)) return fmt;

        if (_formats.Count >= MaxFormats && _formatOrder.Count > 0)
        {
            var old = _formatOrder.Dequeue();
            if (_formats.Remove(old, out nint oldFmt))
            {
                if (_trimSigns.Remove(oldFmt, out nint oldSign)) Release(ref oldSign);
                Release(ref oldFmt);
            }
        }

        fixed (char* pFam = fontFamily)
        fixed (char* pLoc = locale)
        {
            Check(((delegate* unmanaged[Stdcall]<nint, char*, nint, uint, uint, uint, float, char*, nint*, int>)Slot(_write, 15))
                (_write, pFam, 0, (uint)fontWeight, 0, 5 /*NORMAL*/, roundedSize, pLoc, &fmt), "CreateTextFormat");
        }

        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(fmt, 3))(fmt, (uint)hAlign);
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(fmt, 4))(fmt, (uint)vAlign);
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(fmt, 5))(fmt, wrap ? 0u : 1u);

        if (ellipsis)
        {
            nint sign = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_write, 20))(_write, fmt, &sign) == 0 && sign != 0)
            {
                var trim = new DWriteTrimming { Granularity = 1 /*CHARACTER*/, Delimiter = 0, DelimiterCount = 0 };
                ((delegate* unmanaged[Stdcall]<nint, DWriteTrimming*, nint, int>)Slot(fmt, 9))(fmt, &trim, sign);
                _trimSigns[fmt] = sign;
            }
        }

        _formats[key] = fmt;
        _formatOrder.Enqueue(key);
        return fmt;
    }

    public UiTextSize MeasureText(
        string text,
        string fontFamily,
        float fontSize,
        int fontWeight,
        float maxWidth,
        float maxHeight,
        bool wrap)
    {
        if (string.IsNullOrEmpty(text)) return new UiTextSize(0f, fontSize * 1.3f, 1);
        EnsureInit();
        nint fmt = GetTextFormat(fontFamily, fontSize, fontWeight, UiTextAlign.Leading, UiVertAlign.Top, wrap, false);
        nint layout = 0;
        fixed (char* pText = text)
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)Slot(_write, 18))
                (_write, pText, (uint)text.Length, fmt, MathF.Max(1f, maxWidth), MathF.Max(1f, maxHeight), &layout);
            if (hr != 0 || layout == 0) return new UiTextSize(0f, fontSize * 1.3f, 1);
        }
        try
        {
            DWriteTextMetrics metrics = default;
            if (((delegate* unmanaged[Stdcall]<nint, DWriteTextMetrics*, int>)Slot(layout, 60))(layout, &metrics) == 0)
                return new UiTextSize(metrics.WidthIncludingTrailingWhitespace, metrics.Height, (int)Math.Max(1u, metrics.LineCount));
            return new UiTextSize(0f, fontSize * 1.3f, 1);
        }
        finally
        {
            Release(ref layout);
        }
    }

    public nint GetIconGeometry(UiIcon icon)
    {
        EnsureInit();
        if (_iconGeometries.TryGetValue(icon, out nint geom)) return geom;
        string data = UiIcons.GetPathData(icon);
        if (string.IsNullOrEmpty(data)) return 0;

        nint path = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_d2d, 10))(_d2d, &path), "CreatePathGeometry");
        nint sink = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(path, 17))(path, &sink), "PathGeometry.Open");
        try
        {
            BuildPathGeometry(sink, data);
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(sink, 9))(sink), "GeometrySink.Close");
        }
        finally
        {
            Release(ref sink);
        }

        _iconGeometries[icon] = path;
        return path;
    }

    private static void BuildPathGeometry(nint sink, string data)
    {
        var p = new SvgPathParser(data);
        D2dPoint cur = default;
        D2dPoint figStart = default;
        bool inFigure = false;
        char lastCmd = '\0';
        D2dPoint lastQuadCtrl = default;

        while (p.TryReadCommand(out char cmd))
        {
            bool rel = char.IsLower(cmd);
            char u = char.ToUpperInvariant(cmd);
            if (u == 'Z')
            {
                if (inFigure)
                {
                    ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(sink, 8))(sink, 1 /*CLOSED*/);
                    inFigure = false;
                    cur = figStart;
                }
                lastCmd = u;
                continue;
            }

            if (u == 'M')
            {
                float x = p.ReadFloat();
                float y = p.ReadFloat();
                if (rel && inFigure) { x += cur.X; y += cur.Y; }
                else if (rel) { x += cur.X; y += cur.Y; }
                if (inFigure)
                    ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(sink, 8))(sink, 0 /*OPEN*/);
                cur = new D2dPoint(x, y);
                figStart = cur;
                ((delegate* unmanaged[Stdcall]<nint, D2dPoint, uint, void>)Slot(sink, 5))(sink, cur, 0 /*FILLED*/);
                inFigure = true;
                lastCmd = u;
                while (p.HasMoreNumbers())
                {
                    float lx = p.ReadFloat(), ly = p.ReadFloat();
                    if (rel) { lx += cur.X; ly += cur.Y; }
                    cur = new D2dPoint(lx, ly);
                    ((delegate* unmanaged[Stdcall]<nint, D2dPoint, void>)Slot(sink, 10))(sink, cur);
                }
                continue;
            }

            do
            {
                switch (u)
                {
                    case 'L':
                    {
                        float x = p.ReadFloat(), y = p.ReadFloat();
                        if (rel) { x += cur.X; y += cur.Y; }
                        cur = new D2dPoint(x, y);
                        ((delegate* unmanaged[Stdcall]<nint, D2dPoint, void>)Slot(sink, 10))(sink, cur);
                        break;
                    }
                    case 'H':
                    {
                        float x = p.ReadFloat();
                        if (rel) x += cur.X;
                        cur = new D2dPoint(x, cur.Y);
                        ((delegate* unmanaged[Stdcall]<nint, D2dPoint, void>)Slot(sink, 10))(sink, cur);
                        break;
                    }
                    case 'V':
                    {
                        float y = p.ReadFloat();
                        if (rel) y += cur.Y;
                        cur = new D2dPoint(cur.X, y);
                        ((delegate* unmanaged[Stdcall]<nint, D2dPoint, void>)Slot(sink, 10))(sink, cur);
                        break;
                    }
                    case 'Q':
                    {
                        float cx = p.ReadFloat(), cy = p.ReadFloat();
                        float x = p.ReadFloat(), y = p.ReadFloat();
                        if (rel) { cx += cur.X; cy += cur.Y; x += cur.X; y += cur.Y; }
                        lastQuadCtrl = new D2dPoint(cx, cy);
                        cur = new D2dPoint(x, y);
                        var seg = new D2dQuadBezier { Point1 = lastQuadCtrl, Point2 = cur };
                        ((delegate* unmanaged[Stdcall]<nint, D2dQuadBezier*, void>)Slot(sink, 12))(sink, &seg);
                        break;
                    }
                    case 'T':
                    {
                        D2dPoint ctrl = (lastCmd == 'Q' || lastCmd == 'T')
                            ? new D2dPoint(2f * cur.X - lastQuadCtrl.X, 2f * cur.Y - lastQuadCtrl.Y)
                            : cur;
                        float x = p.ReadFloat(), y = p.ReadFloat();
                        if (rel) { x += cur.X; y += cur.Y; }
                        lastQuadCtrl = ctrl;
                        cur = new D2dPoint(x, y);
                        var seg = new D2dQuadBezier { Point1 = ctrl, Point2 = cur };
                        ((delegate* unmanaged[Stdcall]<nint, D2dQuadBezier*, void>)Slot(sink, 12))(sink, &seg);
                        break;
                    }
                    case 'A':
                    {
                        float rx = MathF.Abs(p.ReadFloat());
                        float ry = MathF.Abs(p.ReadFloat());
                        float angle = p.ReadFloat();
                        bool large = p.ReadFlag();
                        bool sweep = p.ReadFlag();
                        float x = p.ReadFloat(), y = p.ReadFloat();
                        if (rel) { x += cur.X; y += cur.Y; }
                        cur = new D2dPoint(x, y);
                        var arc = new D2dArcSegment
                        {
                            Point = cur,
                            Size = new D2dSizeF(rx, ry),
                            RotationAngle = angle,
                            SweepDirection = sweep ? 1u : 0u,
                            ArcSize = large ? 1u : 0u,
                        };
                        ((delegate* unmanaged[Stdcall]<nint, D2dArcSegment*, void>)Slot(sink, 14))(sink, &arc);
                        break;
                    }
                }
                lastCmd = u;
            } while (p.HasMoreNumbers());
        }

        if (inFigure)
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(sink, 8))(sink, 0 /*OPEN*/);
    }

    public void Dispose()
    {
        foreach (nint g in _iconGeometries.Values) { nint tmp = g; Release(ref tmp); }
        _iconGeometries.Clear();
        foreach (nint s in _trimSigns.Values) { nint tmp = s; Release(ref tmp); }
        _trimSigns.Clear();
        foreach (nint f in _formats.Values) { nint tmp = f; Release(ref tmp); }
        _formats.Clear();
        _formatOrder.Clear();
        Release(ref _roundStroke);
        Release(ref _flatStroke);
        Release(ref _write);
        Release(ref _d2d);
    }

    internal static void* Slot(nint com, int index) => (*((void***)com))[index];

    internal static void Release(ref nint com)
    {
        nint p = com;
        com = 0;
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(p, 2))(p);
    }

    internal static void Check(int hr, string api)
    {
        if (hr < 0) throw new COMException($"{api} failed: 0x{hr:X8}", hr);
    }

    [DllImport("d2d1.dll")] private static extern int D2D1CreateFactory(int type, ref Guid iid, nint options, out nint factory);
    [DllImport("dwrite.dll")] private static extern int DWriteCreateFactory(int type, ref Guid iid, out nint factory);

    private readonly record struct TextFormatKey(string Family, string Locale, float Size, int Weight, UiTextAlign H, UiVertAlign V, bool Wrap, bool Ellipsis);

    private ref struct SvgPathParser
    {
        private readonly string _s;
        private int _i;
        private char _currentCmd;

        public SvgPathParser(string s) { _s = s; _i = 0; _currentCmd = '\0'; }

        private void SkipWsAndCommas()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
        }

        public bool TryReadCommand(out char cmd)
        {
            SkipWsAndCommas();
            if (_i >= _s.Length) { cmd = '\0'; return false; }
            char c = _s[_i];
            if (char.IsLetter(c))
            {
                _currentCmd = c;
                _i++;
                cmd = c;
                return true;
            }
            cmd = _currentCmd == 'M' ? 'L' : (_currentCmd == 'm' ? 'l' : _currentCmd);
            return cmd != '\0';
        }

        public bool HasMoreNumbers()
        {
            SkipWsAndCommas();
            if (_i >= _s.Length) return false;
            char c = _s[_i];
            return char.IsDigit(c) || c == '-' || c == '+' || c == '.';
        }

        public float ReadFloat()
        {
            SkipWsAndCommas();
            int start = _i;
            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
            bool seenDot = false;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsDigit(c)) _i++;
                else if (c == '.' && !seenDot) { seenDot = true; _i++; }
                else if ((c == 'e' || c == 'E') && _i > start)
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                }
                else break;
            }
            if (_i == start) return 0f;
            return float.TryParse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        public bool ReadFlag()
        {
            SkipWsAndCommas();
            if (_i < _s.Length && (_s[_i] == '0' || _s[_i] == '1'))
                return _s[_i++] == '1';
            return ReadFloat() != 0f;
        }
    }
}

internal sealed unsafe class D2dTarget : IDisposable
{
    private const int RecreateTarget = unchecked((int)0x8899000C);
    private const int MaxCachedBitmaps = 96;

    private readonly D2dSharedContext _shared;
    private nint _target;
    private nint _brush;
    private nint _layer;
    private readonly Dictionary<object, BitmapEntry> _bitmaps = new();
    private readonly Queue<object> _bitmapOrder = new();
    private readonly Stack<D2dMatrix3x2> _transformStack = new();
    private D2dMatrix3x2 _currentTransform = D2dMatrix3x2.Identity;

    public D2dTarget(D2dSharedContext shared) => _shared = shared;

    public D2dSharedContext Shared => _shared;

    public void BeginDraw(nint hdc, int widthPx, int heightPx, uint dpi)
    {
        EnsureTarget();
        float fDpi = dpi;
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)D2dSharedContext.Slot(_target, 51))(_target, fDpi, fDpi);
        var rc = new D2dWinRect(0, 0, widthPx, heightPx);
        D2dSharedContext.Check(((delegate* unmanaged[Stdcall]<nint, nint, D2dWinRect*, int>)D2dSharedContext.Slot(_target, 57))(_target, hdc, &rc), "BindDC");
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)D2dSharedContext.Slot(_target, 32))(_target, 0 /*PER_PRIMITIVE*/);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)D2dSharedContext.Slot(_target, 34))(_target, 2 /*GRAYSCALE AA for layered surfaces*/);
        _currentTransform = D2dMatrix3x2.Identity;
        SetTransformDirect(_currentTransform);
        ((delegate* unmanaged[Stdcall]<nint, void>)D2dSharedContext.Slot(_target, 48))(_target);
        var clear = new D2dColorF(UiColor.Transparent);
        ((delegate* unmanaged[Stdcall]<nint, D2dColorF*, void>)D2dSharedContext.Slot(_target, 47))(_target, &clear);
    }

    public void EndDraw()
    {
        if (_target == 0) return;
        ulong t1 = 0, t2 = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)D2dSharedContext.Slot(_target, 49))(_target, &t1, &t2);
        if (hr == RecreateTarget)
        {
            ReleaseTargetObjects();
        }
    }

    private void EnsureTarget()
    {
        if (_target != 0) return;
        nint factory = _shared.D2dFactory;
        var props = new D2dRenderTargetProps
        {
            Type = 0,
            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
            Alpha = 1,   // D2D1_ALPHA_MODE_PREMULTIPLIED
            DpiX = 96f,
            DpiY = 96f,
            Usage = 0,
            MinLevel = 0,
        };
        nint target = 0, brush = 0, layer = 0;
        D2dSharedContext.Check(((delegate* unmanaged[Stdcall]<nint, D2dRenderTargetProps*, nint*, int>)D2dSharedContext.Slot(factory, 16))(factory, &props, &target), "CreateDCRenderTarget");
        _target = target;
        var color = new D2dColorF(UiColor.White);
        D2dSharedContext.Check(((delegate* unmanaged[Stdcall]<nint, D2dColorF*, nint, nint*, int>)D2dSharedContext.Slot(_target, 8))(_target, &color, 0, &brush), "CreateSolidColorBrush");
        _brush = brush;
        D2dSharedContext.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)D2dSharedContext.Slot(_target, 13))(_target, 0, &layer), "CreateLayer");
        _layer = layer;
    }

    private nint SetBrushColor(UiColor c)
    {
        var dc = new D2dColorF(c);
        ((delegate* unmanaged[Stdcall]<nint, D2dColorF*, void>)D2dSharedContext.Slot(_brush, 8))(_brush, &dc);
        return _brush;
    }

    public void FillRect(UiRect rect, UiColor color)
    {
        if (color.A == 0 || rect.IsEmpty) return;
        var r = new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom);
        ((delegate* unmanaged[Stdcall]<nint, D2dRectF*, nint, void>)D2dSharedContext.Slot(_target, 17))(_target, &r, SetBrushColor(color));
    }

    public void DrawRect(UiRect rect, UiColor color, float strokeWidth = 1f)
    {
        if (color.A == 0 || rect.IsEmpty) return;
        float half = strokeWidth * 0.5f;
        var r = new D2dRectF(rect.X + half, rect.Y + half, rect.Right - half, rect.Bottom - half);
        ((delegate* unmanaged[Stdcall]<nint, D2dRectF*, nint, float, nint, void>)D2dSharedContext.Slot(_target, 16))(_target, &r, SetBrushColor(color), strokeWidth, 0);
    }

    public void FillRoundedRect(UiRect rect, float radius, UiColor color)
    {
        if (color.A == 0 || rect.IsEmpty) return;
        if (radius <= 0.1f) { FillRect(rect, color); return; }
        float rClamp = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        var rr = new D2dRoundedRect(new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom), rClamp, rClamp);
        ((delegate* unmanaged[Stdcall]<nint, D2dRoundedRect*, nint, void>)D2dSharedContext.Slot(_target, 19))(_target, &rr, SetBrushColor(color));
    }

    public void DrawRoundedRect(UiRect rect, float radius, UiColor color, float strokeWidth = 1f)
    {
        if (color.A == 0 || rect.IsEmpty) return;
        if (radius <= 0.1f) { DrawRect(rect, color, strokeWidth); return; }
        float half = strokeWidth * 0.5f;
        float rClamp = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        var rr = new D2dRoundedRect(new D2dRectF(rect.X + half, rect.Y + half, rect.Right - half, rect.Bottom - half), rClamp, rClamp);
        ((delegate* unmanaged[Stdcall]<nint, D2dRoundedRect*, nint, float, nint, void>)D2dSharedContext.Slot(_target, 18))(_target, &rr, SetBrushColor(color), strokeWidth, 0);
    }

    public void FillEllipse(float cx, float cy, float rx, float ry, UiColor color)
    {
        if (color.A == 0 || rx <= 0f || ry <= 0f) return;
        var e = new D2dEllipse(new D2dPoint(cx, cy), rx, ry);
        ((delegate* unmanaged[Stdcall]<nint, D2dEllipse*, nint, void>)D2dSharedContext.Slot(_target, 21))(_target, &e, SetBrushColor(color));
    }

    public void DrawEllipse(float cx, float cy, float rx, float ry, UiColor color, float strokeWidth = 1f)
    {
        if (color.A == 0 || rx <= 0f || ry <= 0f) return;
        var e = new D2dEllipse(new D2dPoint(cx, cy), rx, ry);
        ((delegate* unmanaged[Stdcall]<nint, D2dEllipse*, nint, float, nint, void>)D2dSharedContext.Slot(_target, 20))(_target, &e, SetBrushColor(color), strokeWidth, _shared.RoundStrokeStyle);
    }

    public void DrawLine(float x1, float y1, float x2, float y2, UiColor color, float strokeWidth = 1f, bool roundCaps = true)
    {
        if (color.A == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, D2dPoint, D2dPoint, nint, float, nint, void>)D2dSharedContext.Slot(_target, 15))
            (_target, new D2dPoint(x1, y1), new D2dPoint(x2, y2), SetBrushColor(color), strokeWidth, roundCaps ? _shared.RoundStrokeStyle : _shared.FlatStrokeStyle);
    }

    public void DrawIcon(UiIcon icon, UiRect box, UiColor color, float strokeWidth = 1.8f, float rotationDeg = 0f)
    {
        if (color.A == 0 || box.IsEmpty) return;
        nint geom = _shared.GetIconGeometry(icon);
        if (geom == 0) return;

        float scale = MathF.Min(box.Width, box.Height) / 24f;
        float ox = box.X + (box.Width - 24f * scale) * 0.5f;
        float oy = box.Y + (box.Height - 24f * scale) * 0.5f;

        D2dMatrix3x2 mat = rotationDeg == 0f
            ? new D2dMatrix3x2(scale, 0f, 0f, scale, ox, oy)
            : ComposeRotationScaleTranslate(scale, rotationDeg, box.CenterX, box.CenterY);

        PushTransform(mat);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, float, nint, void>)D2dSharedContext.Slot(_target, 22))
            (_target, geom, SetBrushColor(color), strokeWidth, _shared.RoundStrokeStyle);
        PopTransform();
    }

    public void FillLinearGradientRect(UiRect rect, float radius, UiColor startColor, UiColor endColor, bool horizontal)
    {
        if (rect.IsEmpty) return;
        D2dGradientStop* stops = stackalloc D2dGradientStop[2];
        stops[0] = new D2dGradientStop { Position = 0f, Color = new D2dColorF(startColor) };
        stops[1] = new D2dGradientStop { Position = 1f, Color = new D2dColorF(endColor) };

        nint stopColl = 0;
        if (((delegate* unmanaged[Stdcall]<nint, D2dGradientStop*, uint, uint, uint, nint*, int>)D2dSharedContext.Slot(_target, 9))(_target, stops, 2, 0 /*GAMMA_2_2*/, 0 /*CLAMP*/, &stopColl) != 0 || stopColl == 0)
            return;

        nint gradBrush = 0;
        var lgProps = new D2dLinearGradProps
        {
            Start = new D2dPoint(rect.X, rect.Y),
            End = horizontal ? new D2dPoint(rect.Right, rect.Y) : new D2dPoint(rect.X, rect.Bottom),
        };
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, D2dLinearGradProps*, nint, nint, nint*, int>)D2dSharedContext.Slot(_target, 10))(_target, &lgProps, 0, stopColl, &gradBrush) == 0 && gradBrush != 0)
            {
                if (radius > 0.1f)
                {
                    float rClamp = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
                    var rr = new D2dRoundedRect(new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom), rClamp, rClamp);
                    ((delegate* unmanaged[Stdcall]<nint, D2dRoundedRect*, nint, void>)D2dSharedContext.Slot(_target, 19))(_target, &rr, gradBrush);
                }
                else
                {
                    var r = new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom);
                    ((delegate* unmanaged[Stdcall]<nint, D2dRectF*, nint, void>)D2dSharedContext.Slot(_target, 17))(_target, &r, gradBrush);
                }
            }
        }
        finally
        {
            D2dSharedContext.Release(ref gradBrush);
            D2dSharedContext.Release(ref stopColl);
        }
    }

    public void FillHueSpectrumRect(UiRect rect, float radius)
    {
        if (rect.IsEmpty) return;
        D2dGradientStop* stops = stackalloc D2dGradientStop[7];
        stops[0] = new D2dGradientStop { Position = 0f / 6f, Color = new D2dColorF(UiColor.FromRgb(255, 0, 0)) };
        stops[1] = new D2dGradientStop { Position = 1f / 6f, Color = new D2dColorF(UiColor.FromRgb(255, 255, 0)) };
        stops[2] = new D2dGradientStop { Position = 2f / 6f, Color = new D2dColorF(UiColor.FromRgb(0, 255, 0)) };
        stops[3] = new D2dGradientStop { Position = 3f / 6f, Color = new D2dColorF(UiColor.FromRgb(0, 255, 255)) };
        stops[4] = new D2dGradientStop { Position = 4f / 6f, Color = new D2dColorF(UiColor.FromRgb(0, 0, 255)) };
        stops[5] = new D2dGradientStop { Position = 5f / 6f, Color = new D2dColorF(UiColor.FromRgb(255, 0, 255)) };
        stops[6] = new D2dGradientStop { Position = 1f, Color = new D2dColorF(UiColor.FromRgb(255, 0, 0)) };

        nint stopColl = 0;
        if (((delegate* unmanaged[Stdcall]<nint, D2dGradientStop*, uint, uint, uint, nint*, int>)D2dSharedContext.Slot(_target, 9))(_target, stops, 7, 0, 0, &stopColl) != 0 || stopColl == 0)
            return;

        nint gradBrush = 0;
        var lgProps = new D2dLinearGradProps { Start = new D2dPoint(rect.X, rect.Y), End = new D2dPoint(rect.Right, rect.Y) };
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, D2dLinearGradProps*, nint, nint, nint*, int>)D2dSharedContext.Slot(_target, 10))(_target, &lgProps, 0, stopColl, &gradBrush) == 0 && gradBrush != 0)
            {
                var rr = new D2dRoundedRect(new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom), radius, radius);
                ((delegate* unmanaged[Stdcall]<nint, D2dRoundedRect*, nint, void>)D2dSharedContext.Slot(_target, 19))(_target, &rr, gradBrush);
            }
        }
        finally
        {
            D2dSharedContext.Release(ref gradBrush);
            D2dSharedContext.Release(ref stopColl);
        }
    }

    public void DrawText(
        string text,
        UiRect rect,
        UiColor color,
        float fontSize = 13f,
        int fontWeight = 400,
        UiTextAlign hAlign = UiTextAlign.Leading,
        UiVertAlign vAlign = UiVertAlign.Center,
        bool wrap = false,
        bool ellipsis = true,
        string fontFamily = UiMetrics.FontFamily)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0 || rect.IsEmpty) return;
        nint fmt = _shared.GetTextFormat(fontFamily, fontSize, fontWeight, hAlign, vAlign, wrap, ellipsis);
        var box = new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom);
        uint options = wrap ? 0u : 2u /*D2D1_DRAW_TEXT_OPTIONS_CLIP*/;
        fixed (char* pText = text)
        {
            ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, D2dRectF*, nint, uint, uint, void>)D2dSharedContext.Slot(_target, 27))
                (_target, pText, (uint)text.Length, fmt, &box, SetBrushColor(color), options, 0);
        }
    }

    public void DrawBgraBitmap(object cacheKey, byte[] bgraPixels, int widthPx, int heightPx, UiRect dstRect, float opacity = 1f, int version = 0)
    {
        if (widthPx <= 0 || heightPx <= 0 || bgraPixels.Length < widthPx * heightPx * 4 || dstRect.IsEmpty) return;
        if (!_bitmaps.TryGetValue(cacheKey, out var entry) || entry.W != widthPx || entry.H != heightPx || entry.Version != version)
        {
            if (entry.Bitmap != 0)
            {
                nint old = entry.Bitmap;
                D2dSharedContext.Release(ref old);
                _bitmaps.Remove(cacheKey);
            }
            if (_bitmaps.Count >= MaxCachedBitmaps && _bitmapOrder.Count > 0)
            {
                var evictKey = _bitmapOrder.Dequeue();
                if (_bitmaps.Remove(evictKey, out var evicted))
                {
                    nint tmp = evicted.Bitmap;
                    D2dSharedContext.Release(ref tmp);
                }
            }

            nint bmp = 0;
            var props = new D2dBitmapProps
            {
                Format = new D2dPixelFormat { Format = 87, AlphaMode = 1 },
                DpiX = 96f,
                DpiY = 96f,
            };
            fixed (byte* pData = bgraPixels)
            {
                int hr = ((delegate* unmanaged[Stdcall]<nint, D2dSizeU, nint, uint, D2dBitmapProps*, nint*, int>)D2dSharedContext.Slot(_target, 4))
                    (_target, new D2dSizeU((uint)widthPx, (uint)heightPx), (nint)pData, (uint)(widthPx * 4), &props, &bmp);
                if (hr != 0 || bmp == 0) return;
            }
            entry = new BitmapEntry(bmp, widthPx, heightPx, version);
            _bitmaps[cacheKey] = entry;
            _bitmapOrder.Enqueue(cacheKey);
        }

        var dst = new D2dRectF(dstRect.X, dstRect.Y, dstRect.Right, dstRect.Bottom);
        var src = new D2dRectF(0f, 0f, widthPx, heightPx);
        ((delegate* unmanaged[Stdcall]<nint, nint, D2dRectF*, float, uint, D2dRectF*, void>)D2dSharedContext.Slot(_target, 26))
            (_target, entry.Bitmap, &dst, opacity, 1 /*LINEAR*/, &src);
    }

    public void PushClip(UiRect rect)
    {
        var r = new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom);
        ((delegate* unmanaged[Stdcall]<nint, D2dRectF*, uint, void>)D2dSharedContext.Slot(_target, 45))(_target, &r, 0 /*PER_PRIMITIVE*/);
    }

    public void PopClip()
    {
        ((delegate* unmanaged[Stdcall]<nint, void>)D2dSharedContext.Slot(_target, 46))(_target);
    }

    public nint PushRoundedLayer(UiRect rect, float radius)
    {
        float r = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        var rr = new D2dRoundedRect(new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom), r, r);
        nint geom = 0;
        ((delegate* unmanaged[Stdcall]<nint, D2dRoundedRect*, nint*, int>)D2dSharedContext.Slot(_shared.D2dFactory, 6))(_shared.D2dFactory, &rr, &geom);
        if (geom == 0) return 0;

        var lp = new D2dLayerParams
        {
            ContentBounds = new D2dRectF(rect.X, rect.Y, rect.Right, rect.Bottom),
            GeometricMask = geom,
            MaskAntialiasMode = 0,
            MaskTransform = D2dMatrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = 0,
            LayerOptions = 0,
        };
        ((delegate* unmanaged[Stdcall]<nint, D2dLayerParams*, nint, void>)D2dSharedContext.Slot(_target, 40))(_target, &lp, _layer);
        return geom;
    }

    public void PopRoundedLayer(nint geom)
    {
        if (geom == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, void>)D2dSharedContext.Slot(_target, 41))(_target);
        D2dSharedContext.Release(ref geom);
    }

    public void PushTransform(D2dMatrix3x2 m)
    {
        _transformStack.Push(_currentTransform);
        _currentTransform = m;
        SetTransformDirect(m);
    }

    public void PopTransform()
    {
        _currentTransform = _transformStack.Count > 0 ? _transformStack.Pop() : D2dMatrix3x2.Identity;
        SetTransformDirect(_currentTransform);
    }

    private void SetTransformDirect(D2dMatrix3x2 m)
    {
        ((delegate* unmanaged[Stdcall]<nint, D2dMatrix3x2*, void>)D2dSharedContext.Slot(_target, 30))(_target, &m);
    }

    private static D2dMatrix3x2 ComposeRotationScaleTranslate(float scale, float deg, float cx, float cy)
    {
        float rad = deg * MathF.PI / 180f;
        float cos = MathF.Cos(rad);
        float sin = MathF.Sin(rad);
        float sCos = scale * cos;
        float sSin = scale * sin;
        // Map (12, 12) in 24x24 icon space to (cx, cy) with rotation
        float tx = cx - (12f * sCos - 12f * sSin);
        float ty = cy - (12f * sSin + 12f * sCos);
        return new D2dMatrix3x2(sCos, sSin, -sSin, sCos, tx, ty);
    }

    private void ReleaseTargetObjects()
    {
        foreach (var e in _bitmaps.Values) { nint tmp = e.Bitmap; D2dSharedContext.Release(ref tmp); }
        _bitmaps.Clear();
        _bitmapOrder.Clear();
        D2dSharedContext.Release(ref _layer);
        D2dSharedContext.Release(ref _brush);
        D2dSharedContext.Release(ref _target);
    }

    public void Dispose() => ReleaseTargetObjects();

    private readonly record struct BitmapEntry(nint Bitmap, int W, int H, int Version);

    [StructLayout(LayoutKind.Sequential)] private struct D2dWinRect(int l, int t, int r, int b) { public int L = l, T = t, R = r, B = b; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dRectF(float l, float t, float r, float b) { public float L = l, T = t, R = r, B = b; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dRoundedRect(D2dRectF r, float rx, float ry) { public D2dRectF Rect = r; public float Rx = rx, Ry = ry; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dEllipse(D2dPoint c, float rx, float ry) { public D2dPoint Center = c; public float Rx = rx, Ry = ry; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dSizeU(uint w, uint h) { public uint W = w, H = h; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dPixelFormat { public uint Format, AlphaMode; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dBitmapProps { public D2dPixelFormat Format; public float DpiX, DpiY; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dRenderTargetProps { public uint Type, Format, Alpha; public float DpiX, DpiY; public uint Usage, MinLevel; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dGradientStop { public float Position; public D2dColorF Color; }
    [StructLayout(LayoutKind.Sequential)] private struct D2dLinearGradProps { public D2dPoint Start, End; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D2dLayerParams
    {
        public D2dRectF ContentBounds;
        public nint GeometricMask;
        public uint MaskAntialiasMode;
        public D2dMatrix3x2 MaskTransform;
        public float Opacity;
        public nint OpacityBrush;
        public uint LayerOptions;
    }
}

[StructLayout(LayoutKind.Sequential)] internal readonly struct D2dPoint(float x, float y) { public readonly float X = x, Y = y; }
[StructLayout(LayoutKind.Sequential)] internal readonly struct D2dSizeF(float w, float h) { public readonly float W = w, H = h; }
[StructLayout(LayoutKind.Sequential)] internal struct D2dQuadBezier { public D2dPoint Point1, Point2; }
[StructLayout(LayoutKind.Sequential)] internal struct D2dArcSegment { public D2dPoint Point; public D2dSizeF Size; public float RotationAngle; public uint SweepDirection; public uint ArcSize; }
[StructLayout(LayoutKind.Sequential)] internal struct D2dStrokeStyleProps { public uint StartCap, EndCap, DashCap, LineJoin; public float MiterLimit; public uint DashStyle; public float DashOffset; }
[StructLayout(LayoutKind.Sequential)] internal readonly struct D2dMatrix3x2(float m11, float m12, float m21, float m22, float dx, float dy)
{
    public readonly float M11 = m11, M12 = m12, M21 = m21, M22 = m22, Dx = dx, Dy = dy;
    public static D2dMatrix3x2 Identity => new(1f, 0f, 0f, 1f, 0f, 0f);
}
[StructLayout(LayoutKind.Sequential)] internal struct DWriteTrimming { public uint Granularity, Delimiter, DelimiterCount; }
[StructLayout(LayoutKind.Sequential)] internal struct DWriteTextMetrics
{
    public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight;
    public uint MaxBidiReorderingDepth, LineCount;
}
