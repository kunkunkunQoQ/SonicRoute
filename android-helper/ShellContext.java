package org.sonicroute.android;

import android.content.AttributionSource;
import android.content.ContentResolver;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.IContentProvider;
import android.os.Build;
import android.os.Process;
import java.lang.reflect.Field;

/**
 * Shell-attributed ContextWrapper for app_process helpers running under UID 2000 (com.android.shell).
 */
public final class ShellContext extends ContextWrapper {
    public static final String PACKAGE_NAME = "com.android.shell";
    private static volatile ShellContext instance;

    private final ContentResolver contentResolver = new ContentResolver(this) {
        @SuppressWarnings("unused")
        protected IContentProvider acquireProvider(Context c, String name) {
            return AndroidWorkarounds.acquireExternalContentProvider(name);
        }

        @SuppressWarnings("unused")
        public boolean releaseProvider(IContentProvider icp) {
            return false;
        }

        @SuppressWarnings("unused")
        protected IContentProvider acquireUnstableProvider(Context c, String name) {
            return null;
        }

        @SuppressWarnings("unused")
        public boolean releaseUnstableProvider(IContentProvider icp) {
            return false;
        }

        @SuppressWarnings("unused")
        public void unstableProviderDied(IContentProvider icp) {
        }
    };

    private ShellContext(Context base) {
        super(base);
    }

    public static ShellContext get() {
        ShellContext current = instance;
        if (current == null) {
            synchronized (ShellContext.class) {
                current = instance;
                if (current == null) {
                    AndroidWorkarounds.ensureThreadInitialized();
                    Context systemContext = AndroidWorkarounds.getSystemContext();
                    if (systemContext == null) {
                        throw new IllegalStateException("System context unavailable in app_process");
                    }
                    current = new ShellContext(systemContext);
                    instance = current;
                }
            }
        }
        return current;
    }

    @Override
    public String getPackageName() {
        return PACKAGE_NAME;
    }

    @Override
    public String getOpPackageName() {
        return PACKAGE_NAME;
    }

    @Override
    public AttributionSource getAttributionSource() {
        if (Build.VERSION.SDK_INT < 31) {
            return null;
        }
        return new AttributionSource.Builder(Process.SHELL_UID)
            .setPackageName(PACKAGE_NAME)
            .build();
    }

    @Override
    public int getDeviceId() {
        return 0;
    }

    @Override
    public Context getApplicationContext() {
        return this;
    }

    @Override
    public Context createPackageContext(String packageName, int flags) {
        return this;
    }

    @Override
    public Context createAttributionContext(String attributionTag) {
        return this;
    }

    @Override
    public Context createDeviceContext(int deviceId) {
        return this;
    }

    @Override
    public ContentResolver getContentResolver() {
        return contentResolver;
    }

    @Override
    public Object getSystemService(String name) {
        Object service = super.getSystemService(name);
        if (service == null) {
            return null;
        }
        if (Context.CLIPBOARD_SERVICE.equals(name)
            || "semclipboard".equals(name)
            || Context.ACTIVITY_SERVICE.equals(name)) {
            bindServiceContext(service, name);
        }
        return service;
    }

    private void bindServiceContext(Object service, String name) {
        Class<?> type = service.getClass();
        while (type != null) {
            try {
                Field field = type.getDeclaredField("mContext");
                field.setAccessible(true);
                field.set(service, this);
                return;
            } catch (NoSuchFieldException ignored) {
                type = type.getSuperclass();
            } catch (IllegalAccessException | RuntimeException error) {
                System.err.println("ShellContext: could not bind mContext on " + name + ": " + error);
                return;
            }
        }
    }
}
