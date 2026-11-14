// SPDX-License-Identifier: MIT
package org.sonicroute.android.model;

import android.graphics.Rect;
import java.util.Objects;

/**
 * Immutable display dimensions, coordinates, and SonicRoute virtual-display sizing helpers.
 */
public final class Geometry {
    public static final int MIN_SIDE = 240;
    public static final int MAX_SIDE = 8192;

    private Geometry() {
    }

    public static final class Size {
        private final int width;
        private final int height;

        public Size(int width, int height) {
            this.width = width;
            this.height = height;
        }

        public int getWidth() {
            return width;
        }

        public int getHeight() {
            return height;
        }

        public int getMaxSide() {
            return Math.max(width, height);
        }

        public boolean isMultipleOf8() {
            return (width & 7) == 0 && (height & 7) == 0;
        }

        public Rect toRect() {
            return new Rect(0, 0, width, height);
        }

        public Size limitMajor(int maxSize) {
            if (maxSize <= 0) {
                return this;
            }
            int alignedMax = maxSize & ~7;
            if (alignedMax <= 0) {
                return this;
            }
            boolean portrait = height > width;
            int major = portrait ? height : width;
            if (major <= alignedMax) {
                return this;
            }
            int minor = portrait ? width : height;
            int scaledMinor = Math.max(8, (int) ((long) alignedMax * minor / major));
            return portrait ? new Size(scaledMinor, alignedMax) : new Size(alignedMax, scaledMinor);
        }

        public Size round8() {
            if (isMultipleOf8()) {
                return this;
            }
            boolean portrait = height > width;
            int major = portrait ? height : width;
            int minor = portrait ? width : height;
            major = Math.max(8, major & ~7);
            minor = Math.max(8, (minor + 4) & ~7);
            if (minor > major) {
                minor = major;
            }
            return portrait ? new Size(minor, major) : new Size(major, minor);
        }

        @Override
        public boolean equals(Object other) {
            if (this == other) {
                return true;
            }
            if (!(other instanceof Size)) {
                return false;
            }
            Size that = (Size) other;
            return width == that.width && height == that.height;
        }

        @Override
        public int hashCode() {
            return Objects.hash(width, height);
        }

        @Override
        public String toString() {
            return width + "x" + height;
        }
    }

    public static final class Point {
        private final int x;
        private final int y;

        public Point(int x, int y) {
            this.x = x;
            this.y = y;
        }

        public int getX() {
            return x;
        }

        public int getY() {
            return y;
        }

        @Override
        public boolean equals(Object other) {
            if (this == other) {
                return true;
            }
            if (!(other instanceof Point)) {
                return false;
            }
            Point that = (Point) other;
            return x == that.x && y == that.y;
        }

        @Override
        public int hashCode() {
            return Objects.hash(x, y);
        }
    }

    public static final class DisplayInfo {
        private final int displayId;
        private final Size size;
        private final int rotation;
        private final int layerStack;
        private final int flags;
        private final int dpi;

        public DisplayInfo(int displayId, Size size, int rotation, int layerStack, int flags, int dpi) {
            this.displayId = displayId;
            this.size = size;
            this.rotation = rotation;
            this.layerStack = layerStack;
            this.flags = flags;
            this.dpi = dpi;
        }

        public int getDisplayId() {
            return displayId;
        }

        public Size getSize() {
            return size;
        }

        public int getRotation() {
            return rotation;
        }

        public int getLayerStack() {
            return layerStack;
        }

        public int getFlags() {
            return flags;
        }

        public int getDpi() {
            return dpi;
        }
    }

    public static final class NewDisplaySpec {
        private final Size size;
        private final int dpi;

        public NewDisplaySpec(Size size, int dpi) {
            this.size = size;
            this.dpi = dpi;
        }

        public Size getSize() {
            return size;
        }

        public int getDpi() {
            return dpi;
        }
    }

    public static int clampAndAlignSide(int value) {
        int clamped = Math.max(MIN_SIDE, Math.min(MAX_SIDE, value));
        int aligned = clamped & ~7;
        return Math.max(MIN_SIDE, aligned);
    }

    /**
     * Fits a host-requested virtual display window shape into the physical display's pixel budget
     * while enforcing [240, 8192] bounds and 8-pixel encoder alignment.
     */
    public static Size fitVirtualDisplaySize(Size phoneMainSize, int requestedWidth, int requestedHeight) {
        if (requestedWidth <= 0 || requestedHeight <= 0) {
            throw new IllegalArgumentException("Invalid window shape");
        }
        if (phoneMainSize == null || phoneMainSize.getWidth() <= 0 || phoneMainSize.getHeight() <= 0) {
            return new Size(clampAndAlignSide(requestedWidth), clampAndAlignSide(requestedHeight));
        }

        int shortEdge = Math.max(MIN_SIDE, Math.min(MAX_SIDE, Math.min(phoneMainSize.getWidth(), phoneMainSize.getHeight())));
        int longEdge = Math.max(shortEdge, Math.min(MAX_SIDE, Math.max(phoneMainSize.getWidth(), phoneMainSize.getHeight())));
        int maxWidth = requestedWidth >= requestedHeight ? longEdge : shortEdge;
        int maxHeight = requestedWidth >= requestedHeight ? shortEdge : longEdge;
        double scale = Math.min((double) maxWidth / requestedWidth, (double) maxHeight / requestedHeight);
        int fittedWidth = Math.max(MIN_SIDE, (int) (requestedWidth * scale) & ~7);
        int fittedHeight = Math.max(MIN_SIDE, (int) (requestedHeight * scale) & ~7);
        return new Size(fittedWidth, fittedHeight);
    }

    /**
     * Maps client coordinates expressed relative to {@code clientScreen} onto {@code targetDisplay}.
     * Returns {@code null} if {@code clientScreen} does not match the active video dimensions.
     */
    public static Point mapClientPoint(
            int x, int y, int clientWidth, int clientHeight, Size expectedVideoSize, Size targetDisplaySize) {
        if (expectedVideoSize == null || targetDisplaySize == null) {
            return null;
        }
        if (clientWidth != expectedVideoSize.getWidth() || clientHeight != expectedVideoSize.getHeight()) {
            return null;
        }
        if (expectedVideoSize.getWidth() <= 0 || expectedVideoSize.getHeight() <= 0) {
            return null;
        }
        int mappedX = (int) ((long) x * targetDisplaySize.getWidth() / expectedVideoSize.getWidth());
        int mappedY = (int) ((long) y * targetDisplaySize.getHeight() / expectedVideoSize.getHeight());
        mappedX = Math.max(0, Math.min(targetDisplaySize.getWidth() - 1, mappedX));
        mappedY = Math.max(0, Math.min(targetDisplaySize.getHeight() - 1, mappedY));
        return new Point(mappedX, mappedY);
    }
}
