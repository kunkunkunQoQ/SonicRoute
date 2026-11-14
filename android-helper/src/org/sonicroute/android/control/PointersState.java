// SPDX-License-Identifier: MIT
package org.sonicroute.android.control;

import android.view.MotionEvent;
import java.util.ArrayList;
import java.util.List;
import org.sonicroute.android.model.Geometry.Point;

/**
 * Tracks up to 10 concurrent touch/mouse pointers and populates Android {@link MotionEvent} arrays.
 */
public final class PointersState {
    public static final int MAX_POINTERS = 10;

    public static final class Pointer {
        private final long id;
        private final int localId;
        private Point point;
        private float pressure;
        private boolean up;

        Pointer(long id, int localId) {
            this.id = id;
            this.localId = localId;
        }

        public long getId() {
            return id;
        }

        public int getLocalId() {
            return localId;
        }

        public Point getPoint() {
            return point;
        }

        public void setPoint(Point point) {
            this.point = point;
        }

        public float getPressure() {
            return pressure;
        }

        public void setPressure(float pressure) {
            this.pressure = pressure;
        }

        public boolean isUp() {
            return up;
        }

        public void setUp(boolean up) {
            this.up = up;
        }
    }

    private final List<Pointer> active = new ArrayList<>(MAX_POINTERS);

    public Pointer get(int index) {
        return active.get(index);
    }

    public int getPointerIndex(long id) {
        for (int i = 0; i < active.size(); ++i) {
            if (active.get(i).getId() == id) {
                return i;
            }
        }
        if (active.size() >= MAX_POINTERS) {
            return -1;
        }
        int localId = findFreeLocalId();
        if (localId < 0) {
            return -1;
        }
        active.add(new Pointer(id, localId));
        return active.size() - 1;
    }

    public int populateArrays(
            MotionEvent.PointerProperties[] props,
            MotionEvent.PointerCoords[] coords,
            int toolType) {
        int count = active.size();
        for (int i = 0; i < count; ++i) {
            Pointer p = active.get(i);
            props[i].clear();
            props[i].id = p.getLocalId();
            props[i].toolType = toolType;

            coords[i].clear();
            Point pt = p.getPoint();
            if (pt != null) {
                coords[i].x = pt.getX();
                coords[i].y = pt.getY();
            }
            coords[i].pressure = p.getPressure();
            coords[i].size = 1.0f;
        }
        for (int i = count - 1; i >= 0; --i) {
            if (active.get(i).isUp()) {
                active.remove(i);
            }
        }
        return count;
    }

    private int findFreeLocalId() {
        for (int candidate = 0; candidate < MAX_POINTERS; ++candidate) {
            boolean used = false;
            for (Pointer p : active) {
                if (p.getLocalId() == candidate) {
                    used = true;
                    break;
                }
            }
            if (!used) {
                return candidate;
            }
        }
        return -1;
    }
}
