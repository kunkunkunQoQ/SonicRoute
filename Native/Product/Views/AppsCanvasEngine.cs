using System;
using System.Collections.Generic;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
#endif

namespace SonicRoute.Native.Product.Views;

internal enum CanvasWireKind { NativeOutput, CaptureForward, MicrophoneInput }

internal sealed class CanvasDeviceNode
{
    public required string NodeKey;      // "render:<id>" or "capture:<id>"
    public required string DeviceId;
    public required string PreferenceId;
    public required string LayoutKey;    // Stable key persisted in AppCanvasLayout.Devices
    public required Flow Flow;
    public UiIcon Icon;
    public string PrimaryName = "";
    public string SecondaryName = "";
    public string FullTooltip = "";
    public bool IsSystemDefault;
    public bool IsActualWindowsDefault;
    public bool IsPhoneOutput;
    public bool IsHiddenFallback;
    public bool IsAvailable = true;
    public int Volume = 100;
    public bool Muted;
    public UiColor WireColor;
    public float X;
    public float Y;
    public float Width = AppsCanvasMetrics.DeviceNodeWidth;
    public float Height = AppsCanvasMetrics.DeviceNodeHeight;
    public bool IsUserPositioned;
    public bool HasAssignedPosition;

    public UiRect Bounds => new(X, Y, Width, Height);
    public UiPoint PortCenter => new(X, Y + Height * 0.5f);
}

internal struct CanvasWire
{
    public required string Key;
    public required CanvasWireKind Kind;
    public required string DeviceId;
    public float X0, Y0, X1, Y1;
    public UiColor Color;
    public bool Dashed;
    public bool Pending;
    public bool Unavailable;
}

internal readonly record struct RememberedNodePosition(float X, float Y, bool UserPositioned);

internal static class AppsCanvasMetrics
{
    public const float AppNodeWidth = 310f;
    public const float DeviceNodeWidth = 242f;
    public const float DeviceNodeHeight = 90f;
    public const float PhoneDeviceNodeHeight = 68f;
    public const float MinZoom = 0.35f;
    public const float MaxZoom = 1.80f;
    public const float DefaultStartX = 28f;
    public const float DefaultStartY = 34f;
    public const float DefaultColumnGap = 96f;
    public const float DefaultDeviceGapX = 22f;
    public const float DefaultDeviceGapY = 12f;
    public const float DefaultSectionGapY = 32f;
    public const float SectionHeaderHeight = 22f;
    public const float CollisionPadX = 10f;
    public const float CollisionPadY = 8f;
    public const float PortRadius = 6f;
    public const float AddPortRadius = 9.5f;
    public const float PortHitRadius = 15f;
    public const float DragThresholdDip = 6f;
    public const float NodeCornerRadius = 12f;
}

/// <summary>
/// Unified partitioned, collision-free canvas layout engine.
/// Handles initial arrangement, auto-arrange, reset, dynamic device addition/removal,
/// phone connect/disconnect, microphone toggle, and app node height changes with one rule set:
/// Playback occupies the upper-right partition; Microphone occupies the lower-right partition
/// strictly below Playback's actual dynamic boundary; user-positioned nodes are always preserved.
/// </summary>
internal static class AppsCanvasLayoutEngine
{
    public static bool Overlaps(UiRect a, UiRect b, float padX = AppsCanvasMetrics.CollisionPadX, float padY = AppsCanvasMetrics.CollisionPadY)
    {
        return a.X < b.Right + padX && a.Right + padX > b.X
            && a.Y < b.Bottom + padY && a.Bottom + padY > b.Y;
    }

    public static bool OverlapsAny(UiRect candidate, IReadOnlyList<UiRect> obstacles,
        float padX = AppsCanvasMetrics.CollisionPadX, float padY = AppsCanvasMetrics.CollisionPadY)
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            if (Overlaps(candidate, obstacles[i], padX, padY))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the highest available collision-free slot in a 1- or 2-column partition starting at zoneTop.
    /// Guaranteed not to overlap any existing obstacle (App Node, Playback nodes, or Microphone nodes).
    /// </summary>
    public static UiPoint FindFreeSlotInPartition(
        float zoneTop,
        float col1X,
        float col2X,
        int maxCols,
        float width,
        float height,
        IReadOnlyList<UiRect> obstacles)
    {
        float FindYInColumn(float colX)
        {
            float y = zoneTop;
            // Walk downward past every blocking obstacle until the slot is strictly clear.
            for (int guard = 0; guard <= obstacles.Count + 2; guard++)
            {
                var cand = new UiRect(colX, y, width, height);
                bool bumped = false;
                for (int i = 0; i < obstacles.Count; i++)
                {
                    var obs = obstacles[i];
                    if (Overlaps(cand, obs))
                    {
                        float nextY = obs.Bottom + AppsCanvasMetrics.DefaultDeviceGapY;
                        if (nextY > y)
                        {
                            y = nextY;
                            bumped = true;
                        }
                    }
                }
                if (!bumped) break;
            }
            return y;
        }

        float y1 = FindYInColumn(col1X);
        if (maxCols <= 1) return new UiPoint(col1X, y1);
        float y2 = FindYInColumn(col2X);
        return y1 <= y2 + 1f ? new UiPoint(col1X, y1) : new UiPoint(col2X, y2);
    }

    /// <summary>
    /// Single unified layout solver for both full arrangement and incremental dynamic updates.
    /// Returns true if any node's coordinates changed.
    /// </summary>
    public static bool Reconcile(
        ref float appX,
        ref float appY,
        float appHeight,
        ref bool appUserPositioned,
        List<CanvasDeviceNode> renderNodes,
        List<CanvasDeviceNode> captureNodes,
        Func<CanvasDeviceNode, int> renderRank,
        Func<CanvasDeviceNode, int> captureRank,
        bool fullArrange,
        IReadOnlyDictionary<string, RememberedNodePosition>? remembered,
        bool allowTwoColumns = true,
        bool restoreSavedStack = false)
    {
        bool changed = false;

        if (fullArrange)
        {
            float newAppX = AppsCanvasMetrics.DefaultStartX;
            float newAppY = AppsCanvasMetrics.DefaultStartY;
            if (MathF.Abs(appX - newAppX) > 0.1f || MathF.Abs(appY - newAppY) > 0.1f) changed = true;
            appX = newAppX;
            appY = newAppY;
            appUserPositioned = false;

            float col1X = appX + AppsCanvasMetrics.AppNodeWidth + AppsCanvasMetrics.DefaultColumnGap;
            float renderTop = AppsCanvasMetrics.DefaultStartY;

            // Read roles across each row before moving down. Unused phone outputs start their own row.
            var sortedRender = SortByRank(renderNodes, renderRank);
            int renderCols = allowTwoColumns && sortedRender.Count > 4 ? 2 : 1;
            int firstOptionalPhone = sortedRender.FindIndex(n => n.IsPhoneOutput && renderRank(n) > 1);
            changed |= PlaceSortedRows(sortedRender, col1X, renderTop, renderCols, firstOptionalPhone);

            // Gently center short playback column against App Node only when there is no Mic section below.
            if (sortedRender.Count > 0 && captureNodes.Count == 0 && renderCols == 1)
            {
                float rBottom = MaxBottom(sortedRender);
                float rSpan = rBottom - renderTop;
                if (rSpan < appHeight)
                {
                    float shift = MathF.Floor((appHeight - rSpan) * 0.5f);
                    if (shift > 1f)
                    {
                        for (int i = 0; i < sortedRender.Count; i++) sortedRender[i].Y += shift;
                        changed = true;
                    }
                }
            }

            // Reorder list cleanly to match arranged order so top-to-bottom traversal is coherent.
            renderNodes.Clear();
            renderNodes.AddRange(sortedRender);

            // Microphone partition: strictly below Playback partition's actual dynamic bottom boundary.
            if (captureNodes.Count > 0)
            {
                float playbackBottom = sortedRender.Count > 0
                    ? MaxBottom(sortedRender)
                    : renderTop - AppsCanvasMetrics.DefaultSectionGapY - AppsCanvasMetrics.SectionHeaderHeight;
                float micZoneTop = sortedRender.Count > 0
                    ? playbackBottom + AppsCanvasMetrics.DefaultSectionGapY + AppsCanvasMetrics.SectionHeaderHeight
                    : renderTop;

                var sortedCapture = SortByRank(captureNodes, captureRank);
                int capCols = allowTwoColumns && sortedCapture.Count > 4 ? 2 : 1;
                changed |= PlaceSortedRows(sortedCapture, col1X, micZoneTop, capCols);
                captureNodes.Clear();
                captureNodes.AddRange(sortedCapture);
            }

            return changed;
        }

        // === Incremental Dynamic Reconcile ===
        // 1. App Node is always an obstacle with its real current height (including dynamic forwarding rows).
        var liveAppRect = new UiRect(appX, appY, AppsCanvasMetrics.AppNodeWidth, appHeight);
        var obstacles = new List<UiRect>(renderNodes.Count + captureNodes.Count + 2) { liveAppRect };

        HashSet<CanvasDeviceNode>? restoredStack = restoreSavedStack ? new() : null;
        bool IsLocked(CanvasDeviceNode n) => n.HasAssignedPosition
            && (n.IsUserPositioned || restoredStack?.Contains(n) == true);

        // 2. Preserve a saved stack when opening the canvas, including the cards underneath it.
        //    During ordinary device reconnects, restore remembered manual positions only if clear.
        void LockPinnedNodes(List<CanvasDeviceNode> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                if (n.HasAssignedPosition && n.IsUserPositioned)
                {
                    obstacles.Add(n.Bounds);
                }
                else if (!n.HasAssignedPosition && remembered != null
                    && remembered.TryGetValue(n.LayoutKey, out var rem) && (restoreSavedStack || rem.UserPositioned))
                {
                    var cand = new UiRect(rem.X, rem.Y, n.Width, n.Height);
                    if (restoreSavedStack || !OverlapsAny(cand, obstacles))
                    {
                        n.X = rem.X;
                        n.Y = rem.Y;
                        n.IsUserPositioned = rem.UserPositioned;
                        n.HasAssignedPosition = true;
                        obstacles.Add(cand);
                        restoredStack?.Add(n);
                        changed = true;
                    }
                }
            }
        }
        LockPinnedNodes(renderNodes);
        LockPinnedNodes(captureNodes);

        // Compute stable right-hand column X anchor without jittering existing auto-arranged nodes.
        float anchorCol1X = ComputeStableColumnX(liveAppRect, renderNodes, captureNodes);
        float anchorCol2X = anchorCol1X + AppsCanvasMetrics.DeviceNodeWidth + AppsCanvasMetrics.DefaultDeviceGapX;
        float rZoneTop = AppsCanvasMetrics.DefaultStartY;

        // 3. Playback Partition (Flow.Render, strictly upper right zone)
        var renderNeedsPlace = new List<CanvasDeviceNode>();
        for (int i = 0; i < renderNodes.Count; i++)
        {
            var n = renderNodes[i];
            if (IsLocked(n)) continue;

            if (n.HasAssignedPosition && !OverlapsAny(n.Bounds, obstacles))
            {
                // Existing auto-placed playback node is still clean and collision-free: keep in place!
                obstacles.Add(n.Bounds);
            }
            else
            {
                renderNeedsPlace.Add(n);
            }
        }

        if (renderNeedsPlace.Count > 0)
        {
            int renderCols = renderNodes.Count > 4 ? 2 : 1;
            var ranked = SortByRank(renderNeedsPlace, renderRank);
            for (int i = 0; i < ranked.Count; i++)
            {
                var n = ranked[i];
                // If reconnecting with a remembered auto position that is still clean & in upper zone, restore it.
                if (!n.HasAssignedPosition && remembered != null
                    && remembered.TryGetValue(n.LayoutKey, out var rem) && !rem.UserPositioned)
                {
                    var cand = new UiRect(rem.X, rem.Y, n.Width, n.Height);
                    if (rem.Y >= rZoneTop - 1f && !OverlapsAny(cand, obstacles))
                    {
                        n.X = rem.X;
                        n.Y = rem.Y;
                        n.IsUserPositioned = false;
                        n.HasAssignedPosition = true;
                        obstacles.Add(cand);
                        changed = true;
                        continue;
                    }
                }

                var pt = FindFreeSlotInPartition(rZoneTop, anchorCol1X, anchorCol2X, renderCols, n.Width, n.Height, obstacles);
                if (!n.HasAssignedPosition || MathF.Abs(n.X - pt.X) > 0.1f || MathF.Abs(n.Y - pt.Y) > 0.1f) changed = true;
                n.X = pt.X;
                n.Y = pt.Y;
                n.IsUserPositioned = false;
                n.HasAssignedPosition = true;
                obstacles.Add(n.Bounds);
            }
        }

        // 4. Compute dynamic Playback partition bottom boundary (never uses fixed Y or guessed device count!).
        bool hasRenderInBand = false;
        float renderBandBottom = rZoneTop;
        for (int i = 0; i < renderNodes.Count; i++)
        {
            var n = renderNodes[i];
            // Include all auto-placed render nodes, plus any pinned render node overlapping the right device columns.
            bool inRightBand = !n.IsUserPositioned || (n.X + n.Width > anchorCol1X - 24f && n.X < anchorCol2X + AppsCanvasMetrics.DeviceNodeWidth + 60f);
            if (!inRightBand) continue;
            float b = n.Y + n.Height;
            if (!hasRenderInBand || b > renderBandBottom)
            {
                renderBandBottom = b;
                hasRenderInBand = true;
            }
        }

        float dynamicMicZoneTop = hasRenderInBand
            ? renderBandBottom + AppsCanvasMetrics.DefaultSectionGapY + AppsCanvasMetrics.SectionHeaderHeight
            : rZoneTop;

        // 5. Microphone Partition (Flow.Capture, strictly below Playback partition)
        if (captureNodes.Count > 0)
        {
            // Check whether existing auto-placed Capture nodes are still cleanly below dynamicMicZoneTop and non-overlapping.
            bool autoCaptureNeedsRepack = false;
            float minExistingAutoCapY = float.MaxValue;
            int existingAutoCapCount = 0;

            var probeObstacles = new List<UiRect>(obstacles);
            for (int i = 0; i < captureNodes.Count; i++)
            {
                var n = captureNodes[i];
                if (IsLocked(n)) continue;
                if (!n.HasAssignedPosition) continue;
                existingAutoCapCount++;
                if (n.Y < minExistingAutoCapY) minExistingAutoCapY = n.Y;
                if (n.Y < dynamicMicZoneTop - 1f || OverlapsAny(n.Bounds, probeObstacles))
                {
                    autoCaptureNeedsRepack = true;
                    break;
                }
                probeObstacles.Add(n.Bounds);
            }

            // If Playback shrank substantially (e.g. phone disconnected), smoothly pull auto-placed Mic nodes up so no giant void remains.
            if (!autoCaptureNeedsRepack && existingAutoCapCount > 0 && minExistingAutoCapY > dynamicMicZoneTop + 72f)
                autoCaptureNeedsRepack = true;

            int capCols = captureNodes.Count > 4 ? 2 : 1;

            if (autoCaptureNeedsRepack)
            {
                // Repack ONLY auto-placed capture nodes starting at dynamicMicZoneTop; user-pinned capture nodes stay locked in obstacles!
                var autoCaps = new List<CanvasDeviceNode>();
                for (int i = 0; i < captureNodes.Count; i++)
                {
                    if (!IsLocked(captureNodes[i]))
                        autoCaps.Add(captureNodes[i]);
                }
                autoCaps.Sort((a, b) =>
                {
                    int ha = a.HasAssignedPosition ? 0 : 1;
                    int hb = b.HasAssignedPosition ? 0 : 1;
                    if (ha != hb) return ha.CompareTo(hb);
                    if (ha == 0)
                    {
                        int yc = a.Y.CompareTo(b.Y);
                        if (MathF.Abs(a.Y - b.Y) > 4f) return yc;
                        return a.X.CompareTo(b.X);
                    }
                    return captureRank(a).CompareTo(captureRank(b));
                });

                for (int i = 0; i < autoCaps.Count; i++)
                {
                    var n = autoCaps[i];
                    var pt = FindFreeSlotInPartition(dynamicMicZoneTop, anchorCol1X, anchorCol2X, capCols, n.Width, n.Height, obstacles);
                    if (!n.HasAssignedPosition || MathF.Abs(n.X - pt.X) > 0.1f || MathF.Abs(n.Y - pt.Y) > 0.1f) changed = true;
                    n.X = pt.X;
                    n.Y = pt.Y;
                    n.IsUserPositioned = false;
                    n.HasAssignedPosition = true;
                    obstacles.Add(n.Bounds);
                }
            }
            else
            {
                // Existing auto-placed capture nodes are already clean: keep them and only place newly arrived / reconnecting capture nodes!
                var capNeedsPlace = new List<CanvasDeviceNode>();
                for (int i = 0; i < captureNodes.Count; i++)
                {
                    var n = captureNodes[i];
                    if (IsLocked(n)) continue;
                    if (n.HasAssignedPosition)
                        obstacles.Add(n.Bounds);
                    else
                        capNeedsPlace.Add(n);
                }

                if (capNeedsPlace.Count > 0)
                {
                    var ranked = SortByRank(capNeedsPlace, captureRank);
                    for (int i = 0; i < ranked.Count; i++)
                    {
                        var n = ranked[i];
                        if (remembered != null && remembered.TryGetValue(n.LayoutKey, out var rem) && !rem.UserPositioned)
                        {
                            var cand = new UiRect(rem.X, rem.Y, n.Width, n.Height);
                            if (rem.Y >= dynamicMicZoneTop - 1f && !OverlapsAny(cand, obstacles))
                            {
                                n.X = rem.X;
                                n.Y = rem.Y;
                                n.IsUserPositioned = false;
                                n.HasAssignedPosition = true;
                                obstacles.Add(cand);
                                changed = true;
                                continue;
                            }
                        }

                        var pt = FindFreeSlotInPartition(dynamicMicZoneTop, anchorCol1X, anchorCol2X, capCols, n.Width, n.Height, obstacles);
                        n.X = pt.X;
                        n.Y = pt.Y;
                        n.IsUserPositioned = false;
                        n.HasAssignedPosition = true;
                        obstacles.Add(n.Bounds);
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    public static bool TryGetSectionHeaderRect(
        IReadOnlyList<CanvasDeviceNode> partitionNodes,
        IReadOnlyList<CanvasDeviceNode> otherNodes,
        UiRect appRect,
        out UiRect headerRect)
    {
        headerRect = default;
        CanvasDeviceNode? best = null;
        float bestGap = float.PositiveInfinity;
        for (int i = 0; i < partitionNodes.Count; i++)
        {
            var node = partitionNodes[i];
            if (!node.HasAssignedPosition) continue;

            // Pick the nearest column first, then the uppermost card in that column.
            float gap = MathF.Max(0f, MathF.Max(appRect.X - node.Bounds.Right, node.X - appRect.Right));
            if (best != null && (gap > bestGap || gap == bestGap &&
                (node.Y > best.Y || node.Y == best.Y && node.X >= best.X))) continue;

            var label = new UiRect(node.X + 2f, node.Y - AppsCanvasMetrics.SectionHeaderHeight, node.Width, 18f);
            if (Overlaps(label, appRect, 2f, 2f) ||
                HeaderOverlapsNodes(label, partitionNodes) || HeaderOverlapsNodes(label, otherNodes))
                continue;

            best = node;
            bestGap = gap;
            headerRect = label;
        }
        return best != null;
    }

    private static bool HeaderOverlapsNodes(UiRect label, IReadOnlyList<CanvasDeviceNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].HasAssignedPosition && Overlaps(label, nodes[i].Bounds, 2f, 2f))
                return true;
        return false;
    }

    private static float ComputeStableColumnX(UiRect appRect, IReadOnlyList<CanvasDeviceNode> renderNodes, IReadOnlyList<CanvasDeviceNode> captureNodes)
    {
        float minAutoX = float.MaxValue;
        for (int i = 0; i < renderNodes.Count; i++)
        {
            var n = renderNodes[i];
            if (n.HasAssignedPosition && !n.IsUserPositioned && n.X >= appRect.Right + 24f && n.X < minAutoX)
                minAutoX = n.X;
        }
        if (minAutoX < float.MaxValue) return minAutoX;
        for (int i = 0; i < captureNodes.Count; i++)
        {
            var n = captureNodes[i];
            if (n.HasAssignedPosition && !n.IsUserPositioned && n.X >= appRect.Right + 24f && n.X < minAutoX)
                minAutoX = n.X;
        }
        if (minAutoX < float.MaxValue) return minAutoX;
        return MathF.Max(
            AppsCanvasMetrics.DefaultStartX + AppsCanvasMetrics.AppNodeWidth + AppsCanvasMetrics.DefaultColumnGap,
            appRect.Right + AppsCanvasMetrics.DefaultColumnGap);
    }

    private static float MaxBottom(IReadOnlyList<CanvasDeviceNode> nodes)
    {
        float max = float.MinValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            float b = nodes[i].Y + nodes[i].Height;
            if (b > max) max = b;
        }
        return max;
    }

    private static bool PlaceSortedRows(List<CanvasDeviceNode> nodes, float firstX, float y, int columns, int rowBreak = -1)
    {
        bool changed = false;
        int column = 0;
        float rowHeight = 0f;
        float columnPitch = AppsCanvasMetrics.DeviceNodeWidth + AppsCanvasMetrics.DefaultDeviceGapX;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            float x = firstX + column * columnPitch;
            if (!n.HasAssignedPosition || MathF.Abs(n.X - x) > 0.1f || MathF.Abs(n.Y - y) > 0.1f) changed = true;
            n.X = x;
            n.Y = y;
            n.IsUserPositioned = false;
            n.HasAssignedPosition = true;

            rowHeight = MathF.Max(rowHeight, n.Height);
            column++;
            if (column == columns || i + 1 == rowBreak)
            {
                y += rowHeight + AppsCanvasMetrics.DefaultDeviceGapY;
                column = 0;
                rowHeight = 0f;
            }
        }
        return changed;
    }

    private static List<CanvasDeviceNode> SortByRank(IReadOnlyList<CanvasDeviceNode> source, Func<CanvasDeviceNode, int> rank)
    {
        var ranked = new List<(CanvasDeviceNode Node, int Rank)>(source.Count);
        for (int i = 0; i < source.Count; i++)
            ranked.Add((source[i], rank(source[i])));
        ranked.Sort((a, b) =>
        {
            int r = a.Rank.CompareTo(b.Rank);
            if (r != 0) return r;
            int name = StringComparer.CurrentCultureIgnoreCase.Compare(a.Node.PrimaryName, b.Node.PrimaryName);
            return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(a.Node.DeviceId, b.Node.DeviceId);
        });
        var result = new List<CanvasDeviceNode>(source.Count);
        for (int i = 0; i < ranked.Count; i++) result.Add(ranked[i].Node);
        return result;
    }
}

/// <summary>
/// Manages cached ID2D1PathGeometry instances for active routing wires.
/// Geometry is only rebuilt when wire endpoints actually change, and released when deactivated/disposed.
/// </summary>
internal sealed class CanvasWireGeometryCache : IDisposable
{
    private struct Entry
    {
        public float X0, Y0, X1, Y1;
        public nint Geometry;
        public int Stamp;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _stamp;

    public void BeginFrame() => _stamp++;

    public nint GetOrUpdate(CanvasWire wire)
    {
        if (_entries.TryGetValue(wire.Key, out var e)
            && MathF.Abs(e.X0 - wire.X0) < 0.25f && MathF.Abs(e.Y0 - wire.Y0) < 0.25f
            && MathF.Abs(e.X1 - wire.X1) < 0.25f && MathF.Abs(e.Y1 - wire.Y1) < 0.25f
            && e.Geometry != 0)
        {
            e.Stamp = _stamp;
            _entries[wire.Key] = e;
            return e.Geometry;
        }

        if (e.Geometry != 0)
        {
            nint old = e.Geometry;
            D2dSharedContext.ReleaseGeometry(ref old);
        }

        ComputeControlPoints(wire.X0, wire.Y0, wire.X1, wire.Y1, out float cx1, out float cy1, out float cx2, out float cy2);
        nint geom = D2dSharedContext.Instance.CreateCubicBezierGeometry(wire.X0, wire.Y0, cx1, cy1, cx2, cy2, wire.X1, wire.Y1);
        _entries[wire.Key] = new Entry { X0 = wire.X0, Y0 = wire.Y0, X1 = wire.X1, Y1 = wire.Y1, Geometry = geom, Stamp = _stamp };
        return geom;
    }

    public void EndFramePrune()
    {
        List<string>? stale = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.Stamp != _stamp)
                (stale ??= new()).Add(pair.Key);
        }
        if (stale == null) return;
        foreach (var key in stale)
        {
            if (_entries.Remove(key, out var e) && e.Geometry != 0)
            {
                nint g = e.Geometry;
                D2dSharedContext.ReleaseGeometry(ref g);
            }
        }
    }

    public void Clear()
    {
        foreach (var e in _entries.Values)
        {
            nint g = e.Geometry;
            D2dSharedContext.ReleaseGeometry(ref g);
        }
        _entries.Clear();
    }

    public void Dispose() => Clear();

    public static void ComputeControlPoints(float x0, float y0, float x1, float y1,
        out float cx1, out float cy1, out float cx2, out float cy2)
    {
        float dx = x1 - x0;
        float handle = dx >= 0f
            ? Math.Clamp(dx * 0.48f, 48f, 180f)
            : Math.Clamp(MathF.Abs(dx) * 0.35f + 64f, 64f, 190f);
        cx1 = x0 + handle;
        cy1 = y0;
        cx2 = x1 - handle;
        cy2 = y1;
    }

    public static float DistanceSquaredToWire(CanvasWire wire, float wx, float wy)
    {
        ComputeControlPoints(wire.X0, wire.Y0, wire.X1, wire.Y1, out float cx1, out float cy1, out float cx2, out float cy2);
        float minDistSq = float.MaxValue;
        const int steps = 16;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            EvaluateCubic(wire.X0, wire.Y0, cx1, cy1, cx2, cy2, wire.X1, wire.Y1, t, out float px, out float py);
            float ddx = wx - px;
            float ddy = wy - py;
            float d2 = ddx * ddx + ddy * ddy;
            if (d2 < minDistSq) minDistSq = d2;
        }
        return minDistSq;
    }

    public static void EvaluateCubic(float x0, float y0, float cx1, float cy1, float cx2, float cy2, float x1, float y1, float t, out float x, out float y)
    {
        float u = 1f - t;
        float tt = t * t;
        float uu = u * u;
        float uuu = uu * u;
        float ttt = tt * t;
        x = uuu * x0 + 3f * uu * t * cx1 + 3f * u * tt * cx2 + ttt * x1;
        y = uuu * y0 + 3f * uu * t * cy1 + 3f * u * tt * cy2 + ttt * y1;
    }

    public static void DrawDirectionChevron(D2dTarget g, float x0, float y0, float x1, float y1, bool reverseToLeft, UiColor color, float strokeWidth = 1.9f)
    {
        ComputeControlPoints(x0, y0, x1, y1, out float cx1, out float cy1, out float cx2, out float cy2);
        float t = reverseToLeft ? 0.16f : 0.84f;
        float dt = reverseToLeft ? -0.04f : 0.04f;
        EvaluateCubic(x0, y0, cx1, cy1, cx2, cy2, x1, y1, t, out float tipX, out float tipY);
        EvaluateCubic(x0, y0, cx1, cy1, cx2, cy2, x1, y1, Math.Clamp(t - dt, 0f, 1f), out float prevX, out float prevY);
        float vx = tipX - prevX;
        float vy = tipY - prevY;
        float len = MathF.Sqrt(vx * vx + vy * vy);
        if (len < 0.001f) return;
        vx /= len;
        vy /= len;
        float nx = -vy;
        float ny = vx;
        const float wingLen = 5.5f;
        const float wingSpread = 3.8f;
        float backX = tipX - vx * wingLen;
        float backY = tipY - vy * wingLen;
        g.DrawLine(backX + nx * wingSpread, backY + ny * wingSpread, tipX, tipY, color, strokeWidth);
        g.DrawLine(backX - nx * wingSpread, backY - ny * wingSpread, tipX, tipY, color, strokeWidth);
    }
}

internal static class AppsCanvasPalette
{
    private static readonly UiColor[] DarkDeviceColors =
    [
        UiColor.FromRgb(0x60, 0xA5, 0xFA), // Sky Blue
        UiColor.FromRgb(0xA7, 0x8B, 0xFA), // Violet
        UiColor.FromRgb(0xFB, 0x92, 0x3C), // Warm Orange
        UiColor.FromRgb(0x38, 0xBD, 0xF8), // Cyan
        UiColor.FromRgb(0xF4, 0x72, 0xB6), // Rose
        UiColor.FromRgb(0xFB, 0xBF, 0x24), // Amber
        UiColor.FromRgb(0x81, 0x8C, 0xF8), // Indigo
    ];

    private static readonly UiColor[] LightDeviceColors =
    [
        UiColor.FromRgb(0x25, 0x63, 0xEB), // Blue
        UiColor.FromRgb(0x7C, 0x3A, 0xED), // Violet
        UiColor.FromRgb(0xEA, 0x58, 0x0C), // Orange
        UiColor.FromRgb(0x02, 0x84, 0xC7), // Cyan
        UiColor.FromRgb(0xDB, 0x27, 0x77), // Rose
        UiColor.FromRgb(0xD9, 0x77, 0x06), // Amber
        UiColor.FromRgb(0x4F, 0x46, 0xE5), // Indigo
    ];

    public static UiColor MicWireColor(UiThemePalette t) =>
        t.IsDark ? UiColor.FromRgb(0x34, 0xD3, 0x99) : UiColor.FromRgb(0x05, 0x96, 0x69);

    public static UiColor DeviceWireColor(UiThemePalette t, Flow flow, string stableKey, bool isSystemDefault)
    {
        if (flow == Flow.Capture) return MicWireColor(t);
        if (isSystemDefault) return t.Accent;
        var palette = t.IsDark ? DarkDeviceColors : LightDeviceColors;
        uint hash = 2166136261u;
        for (int i = 0; i < stableKey.Length; i++)
        {
            hash ^= char.ToUpperInvariant(stableKey[i]);
            hash *= 16777619u;
        }
        return palette[hash % (uint)palette.Length];
    }

    public static UiIcon ClassifyDeviceIcon(Flow flow, string id, string rawName)
    {
#if SONICROUTE_PHONE
        if (id.StartsWith(ExternalAudioBridge.AirPlaySourcePrefix, StringComparison.Ordinal)) return UiIcon.AirPlay;
        if (AndroidOutputIdentity.IsPhoneOutput(id) || id == AndroidAudioSession.SourcePhoneOutputId) return UiIcon.Phone;
#endif
        if (flow == Flow.Capture) return UiIcon.Microphone;
        if (AudioService.IsSystemDefault(id) || rawName.Contains("HDMI", StringComparison.OrdinalIgnoreCase)
            || rawName.Contains("Display", StringComparison.OrdinalIgnoreCase))
            return UiIcon.Monitor;
        if (rawName.Contains("head", StringComparison.OrdinalIgnoreCase)
            || rawName.Contains("ear", StringComparison.OrdinalIgnoreCase)
            || rawName.Contains("耳机") || rawName.Contains("耳機")
            || rawName.Contains("蓝牙") || rawName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            return UiIcon.Headphones;
        return UiIcon.Speaker;
    }

    public static (string Primary, string Secondary) SplitFriendlyName(string rawName)
    {
        string full = rawName ?? "";
        int split = full.IndexOf(" (", StringComparison.Ordinal);
        if (split > 0 && full.EndsWith(")", StringComparison.Ordinal))
            return (full[..split].Trim(), full.Substring(split + 2, full.Length - split - 3).Trim());
        return (full.Trim(), "");
    }
}
