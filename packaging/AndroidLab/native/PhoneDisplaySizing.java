/* SonicRoute phone-resolution budget for owned virtual displays. SPDX-License-Identifier: Apache-2.0 */
package com.genymobile.scrcpy.video;

import com.genymobile.scrcpy.device.Size;

/** The window supplies the shape; the phone supplies the pixel budget. No device queries on resize. */
public final class PhoneDisplaySizing {
    private PhoneDisplaySizing() { }

    public static Size fit(Size phone, int width, int height) {
        if (width <= 0 || height <= 0) {
            throw new IllegalArgumentException("Invalid window shape");
        }
        int shortEdge = Math.max(240, Math.min(8192, Math.min(phone.getWidth(), phone.getHeight())));
        int longEdge = Math.max(shortEdge, Math.min(8192, Math.max(phone.getWidth(), phone.getHeight())));
        int maxWidth = width >= height ? longEdge : shortEdge;
        int maxHeight = width >= height ? shortEdge : longEdge;
        double scale = Math.min((double) maxWidth / width, (double) maxHeight / height);
        int fittedWidth = Math.max(240, (int) (width * scale) & ~7);
        int fittedHeight = Math.max(240, (int) (height * scale) & ~7);
        return new Size(fittedWidth, fittedHeight);
    }
}
