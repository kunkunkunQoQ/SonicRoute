package org.sonicroute.android;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.util.Base64;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.util.List;
import java.util.ArrayList;

/** Read-only metadata helper. Launched by app_process; never installed as an APK. */
public final class Catalog {
    public static void main(String[] args) throws Exception {
        try {
            android.os.Looper.prepareMainLooper();
            Class.forName("com.genymobile.scrcpy.Workarounds").getMethod("apply").invoke(null);
            Context context = (Context) Class.forName("com.genymobile.scrcpy.FakeContext").getMethod("get").invoke(null);
            PackageManager manager = context.getPackageManager();
            List<ApplicationInfo> applications;
            if (args.length > 0) {
                if (!"--packages".equals(args[0]) || args.length > 1001) throw new IllegalArgumentException("Invalid app allowlist");
                applications = new ArrayList<>();
                for (int i = 1; i < args.length; ++i) {
                    if (!args[i].matches("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+)+")) throw new IllegalArgumentException("Invalid package");
                    try { applications.add(manager.getApplicationInfo(args[i], PackageManager.GET_META_DATA)); }
                    catch (PackageManager.NameNotFoundException ignored) { }
                }
            } else {
                applications = manager.getInstalledApplications(PackageManager.GET_META_DATA);
            }
            int count = 0;
            for (ApplicationInfo info : applications) {
                if (!info.enabled || manager.getLaunchIntentForPackage(info.packageName) == null) continue;
                JSONObject app = new JSONObject();
                app.put("package", info.packageName);
                app.put("name", manager.getApplicationLabel(info).toString());
                app.put("uid", info.uid);
                app.put("category", info.category);
                try {
                    Drawable drawable = manager.getApplicationIcon(info);
                    Bitmap bitmap = Bitmap.createBitmap(64, 64, Bitmap.Config.ARGB_8888);
                    drawable.setBounds(0, 0, 64, 64);
                    drawable.draw(new Canvas(bitmap));
                    ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                    bitmap.compress(Bitmap.CompressFormat.PNG, 100, bytes);
                    bitmap.recycle();
                    app.put("icon", Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP));
                } catch (Exception ignored) { /* name/package remain usable without an icon */ }
                System.out.println(app.toString());
                if (++count >= 1000) break;
            }
            System.exit(0);
        } catch (Throwable error) {
            Throwable cause = error;
            while (cause.getCause() != null) cause = cause.getCause();
            error.printStackTrace(System.err);
            System.out.println(new JSONObject().put("error", cause.toString()).toString());
            System.exit(1);
        }
    }
}
