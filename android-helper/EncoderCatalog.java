package org.sonicroute.android;

import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.os.Build;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.TreeSet;

/** A one-shot query, run only when the user opens an encoder picker. */
public final class EncoderCatalog {
    public static void main(String[] args) throws Exception {
        JSONObject result = new JSONObject();
        TreeSet<String> audio = new TreeSet<>(), video = new TreeSet<>();
        JSONObject details = new JSONObject();
        for (MediaCodecInfo codec : new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
            if (!codec.isEncoder() || codec.isAlias()) continue;
            boolean matched = false;
            for (String type : codec.getSupportedTypes()) {
                if ("audio/opus".equalsIgnoreCase(type)) { audio.add(codec.getName()); matched = true; }
                if ("video/avc".equalsIgnoreCase(type)) { video.add(codec.getName()); matched = true; }
            }
            if (matched && Build.VERSION.SDK_INT >= 29) {
                JSONObject meta = new JSONObject();
                meta.put("hw", codec.isHardwareAccelerated());
                meta.put("sw", codec.isSoftwareOnly());
                details.put(codec.getName(), meta);
            }
        }
        result.put("audio", new JSONArray(audio));
        result.put("video", new JSONArray(video));
        result.put("details", details);
        System.out.println(result.toString());
    }
}
