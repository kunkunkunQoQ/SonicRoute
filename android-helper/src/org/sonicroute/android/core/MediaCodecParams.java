// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.core;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Parses typed {@code MediaFormat} key/value pairs and opens Android {@link MediaCodec} encoders.
 */
public final class MediaCodecParams {
    public static final class Entry {
        private final String key;
        private final Object value;

        public Entry(String key, Object value) {
            this.key = key;
            this.value = value;
        }

        public String getKey() {
            return key;
        }

        public Object getValue() {
            return value;
        }
    }

    private MediaCodecParams() {
    }

    public static List<Entry> parse(String spec) {
        if (spec == null || spec.isEmpty()) {
            return Collections.emptyList();
        }
        List<Entry> result = new ArrayList<>();
        StringBuilder current = new StringBuilder();
        boolean escaped = false;
        for (int i = 0; i < spec.length(); ++i) {
            char c = spec.charAt(i);
            if (escaped) {
                current.append(c);
                escaped = false;
            } else if (c == '\\') {
                escaped = true;
            } else if (c == ',') {
                result.add(parseEntry(current.toString()));
                current.setLength(0);
            } else {
                current.append(c);
            }
        }
        if (current.length() > 0) {
            result.add(parseEntry(current.toString()));
        }
        return result;
    }

    private static Entry parseEntry(String raw) {
        int eq = raw.indexOf('=');
        if (eq <= 0) {
            throw new IllegalArgumentException("Expected key[:type]=value in codec option: " + raw);
        }
        String keyAndType = raw.substring(0, eq);
        String valueText = raw.substring(eq + 1);
        int colon = keyAndType.indexOf(':');
        String key = colon >= 0 ? keyAndType.substring(0, colon) : keyAndType;
        String type = colon >= 0 ? keyAndType.substring(colon + 1) : "int";
        Object value;
        switch (type) {
            case "int":
                value = Integer.valueOf(valueText);
                break;
            case "long":
                value = Long.valueOf(valueText);
                break;
            case "float":
                value = Float.valueOf(valueText);
                break;
            case "string":
                value = valueText;
                break;
            default:
                throw new IllegalArgumentException("Unsupported codec option type \"" + type + "\"");
        }
        return new Entry(key, value);
    }

    public static void apply(MediaFormat format, List<Entry> options) {
        if (options == null || options.isEmpty()) {
            return;
        }
        for (Entry entry : options) {
            Object v = entry.getValue();
            if (v instanceof Integer) {
                format.setInteger(entry.getKey(), (Integer) v);
            } else if (v instanceof Long) {
                format.setLong(entry.getKey(), (Long) v);
            } else if (v instanceof Float) {
                format.setFloat(entry.getKey(), (Float) v);
            } else if (v instanceof String) {
                format.setString(entry.getKey(), (String) v);
            }
        }
    }

    public static MediaCodec createEncoder(String mimeType, String requestedName) throws IOException {
        if (requestedName != null && !requestedName.isEmpty()) {
            return MediaCodec.createByCodecName(requestedName);
        }
        try {
            return MediaCodec.createEncoderByType(mimeType);
        } catch (IOException | IllegalArgumentException primaryError) {
            MediaCodecList list = new MediaCodecList(MediaCodecList.REGULAR_CODECS);
            for (MediaCodecInfo info : list.getCodecInfos()) {
                if (!info.isEncoder()) {
                    continue;
                }
                for (String type : info.getSupportedTypes()) {
                    if (mimeType.equalsIgnoreCase(type)) {
                        return MediaCodec.createByCodecName(info.getName());
                    }
                }
            }
            throw primaryError;
        }
    }
}
