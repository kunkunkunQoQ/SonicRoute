package org.sonicroute.android;

import android.app.Application;
import android.app.Instrumentation;
import android.content.AttributionSource;
import android.content.Context;
import android.content.IContentProvider;
import android.content.pm.ApplicationInfo;
import android.media.AudioAttributes;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.os.Binder;
import android.os.Build;
import android.os.IBinder;
import android.os.IInterface;
import android.os.Looper;
import android.os.Parcel;
import java.lang.ref.WeakReference;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * Bootstrap workarounds for standalone Android helpers launched via app_process.
 */
public final class AndroidWorkarounds {
    private static Class<?> activityThreadClass;
    private static Object activityThread;
    private static boolean threadPrepared;
    private static boolean applied;

    private static IInterface activityManager;
    private static Method getContentProviderExternalMethod;
    private static boolean getContentProviderExternalFourArgs;

    private AndroidWorkarounds() {
    }

    static synchronized void ensureThreadInitialized() {
        if (threadPrepared) {
            return;
        }
        try {
            activityThreadClass = Class.forName("android.app.ActivityThread");
            Object existing = null;
            try {
                Method currentMethod = activityThreadClass.getDeclaredMethod("currentActivityThread");
                currentMethod.setAccessible(true);
                existing = currentMethod.invoke(null);
            } catch (Throwable ignored) {
            }

            if (existing != null) {
                activityThread = existing;
            } else {
                Constructor<?> ctor = activityThreadClass.getDeclaredConstructor();
                ctor.setAccessible(true);
                activityThread = ctor.newInstance();

                Field sCurrent = activityThreadClass.getDeclaredField("sCurrentActivityThread");
                sCurrent.setAccessible(true);
                sCurrent.set(null, activityThread);

                Field mSystemThread = activityThreadClass.getDeclaredField("mSystemThread");
                mSystemThread.setAccessible(true);
                mSystemThread.setBoolean(activityThread, true);
            }
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException("Unable to initialize ActivityThread for app_process", error);
        }

        // ConfigurationController was introduced in Android 12 (API 31) and must be populated
        // before getSystemContext() / DisplayManagerGlobal queries ActivityThread.getConfiguration().
        if (Build.VERSION.SDK_INT >= 31) {
            installConfigurationController();
        }

        // Some e-ink devices (e.g., ONYX) misbehave on virtual/mirror display capture if
        // mBoundApplication is populated in a standalone shell process.
        if (!"ONYX".equalsIgnoreCase(Build.BRAND)) {
            installBoundApplication();
        }

        threadPrepared = true;
    }

    public static synchronized void apply() {
        ensureThreadInitialized();
        if (applied) {
            return;
        }
        applied = true;
        installInitialApplication(ShellContext.get());
    }

    static Context getSystemContext() {
        ensureThreadInitialized();
        try {
            Method getSystemContext = activityThreadClass.getDeclaredMethod("getSystemContext");
            getSystemContext.setAccessible(true);
            return (Context) getSystemContext.invoke(activityThread);
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: getSystemContext failed: " + error);
            return null;
        }
    }

    public static void ensureMediaFrameworkInitialized() {
        try {
            Class<?> initializer = Class.forName("android.media.MediaFrameworkPlatformInitializer");
            if (initializer.getMethod("getMediaServiceManager").invoke(null) == null) {
                Class<?> serviceManager = Class.forName("android.media.MediaServiceManager");
                initializer.getMethod("setMediaServiceManager", serviceManager)
                    .invoke(null, serviceManager.getConstructor().newInstance());
            }
        } catch (ClassNotFoundException ignored) {
            // Android versions prior to the media module apex initialize via ServiceManager directly.
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: media framework init failed: " + error);
        }
    }

    static synchronized IContentProvider acquireExternalContentProvider(String name) {
        if (name == null || name.isEmpty()) {
            return null;
        }
        try {
            IInterface am = getActivityManager();
            if (am == null) {
                return null;
            }
            Method method = getGetContentProviderExternalMethod(am);
            Object[] args = getContentProviderExternalFourArgs
                ? new Object[] { name, 0, new Binder(), null }
                : new Object[] { name, 0, new Binder() };
            Object holder = method.invoke(am, args);
            if (holder == null) {
                return null;
            }
            Field providerField = holder.getClass().getDeclaredField("provider");
            providerField.setAccessible(true);
            return (IContentProvider) providerField.get(holder);
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: acquireExternalContentProvider(" + name + ") failed: " + error);
            return null;
        }
    }

    /**
     * Constructs an AudioRecord via native_setup so vendor ROM constructor hooks requiring
     * an installed APK Application context do not block shell-UID capture.
     */
    public static AudioRecord createAudioRecord(
            int source, int sampleRate, int channelConfig, int channels, int channelMask, int encoding) {
        try {
            Constructor<AudioRecord> ctor = AudioRecord.class.getDeclaredConstructor(long.class);
            ctor.setAccessible(true);
            AudioRecord record = ctor.newInstance(0L);

            setField(AudioRecord.class, record, "mRecordingState", AudioRecord.RECORDSTATE_STOPPED);

            Looper looper = Looper.myLooper();
            if (looper == null) {
                looper = Looper.getMainLooper();
            }
            setField(AudioRecord.class, record, "mInitializationLooper", looper);

            AudioAttributes.Builder attrBuilder = new AudioAttributes.Builder();
            Method setPreset = AudioAttributes.Builder.class.getMethod("setInternalCapturePreset", int.class);
            setPreset.invoke(attrBuilder, source);
            AudioAttributes attributes = attrBuilder.build();
            setField(AudioRecord.class, record, "mAudioAttributes", attributes);

            Method paramCheck = AudioRecord.class.getDeclaredMethod("audioParamCheck", int.class, int.class, int.class);
            paramCheck.setAccessible(true);
            paramCheck.invoke(record, source, sampleRate, encoding);

            setField(AudioRecord.class, record, "mChannelCount", channels);
            setField(AudioRecord.class, record, "mChannelMask", channelMask);

            int minBufferSize = AudioRecord.getMinBufferSize(sampleRate, channelConfig, encoding);
            int bufferSizeInBytes = Math.max(minBufferSize, 4096) * 8;
            Method buffCheck = AudioRecord.class.getDeclaredMethod("audioBuffSizeCheck", int.class);
            buffCheck.setAccessible(true);
            buffCheck.invoke(record, bufferSizeInBytes);

            int[] sampleRateOut = new int[] { sampleRate };
            int[] sessionOut = new int[] { AudioManager.AUDIO_SESSION_ID_GENERATE };
            int initResult = invokeNativeSetup(record, attributes, sampleRateOut, channelMask, bufferSizeInBytes, sessionOut);
            if (initResult != AudioRecord.SUCCESS) {
                throw new IllegalStateException("AudioRecord native_setup failed with code " + initResult);
            }

            setField(AudioRecord.class, record, "mSampleRate", sampleRateOut[0]);
            setField(AudioRecord.class, record, "mSessionId", sessionOut[0]);
            setField(AudioRecord.class, record, "mState", AudioRecord.STATE_INITIALIZED);
            return record;
        } catch (RuntimeException error) {
            throw error;
        } catch (Exception error) {
            throw new IllegalStateException("Unable to construct AudioRecord", error);
        }
    }

    private static int invokeNativeSetup(
            AudioRecord record,
            AudioAttributes attributes,
            int[] sampleRateOut,
            int channelMask,
            int bufferSizeInBytes,
            int[] sessionOut) throws Exception {
        WeakReference<AudioRecord> selfRef = new WeakReference<>(record);
        int audioFormat = record.getAudioFormat();
        if (Build.VERSION.SDK_INT < 31) {
            Method nativeSetup = AudioRecord.class.getDeclaredMethod(
                "native_setup", Object.class, Object.class, int[].class, int.class,
                int.class, int.class, int.class, int[].class, String.class, long.class);
            nativeSetup.setAccessible(true);
            return ((Integer) nativeSetup.invoke(
                record, selfRef, attributes, sampleRateOut, channelMask, 0,
                audioFormat, bufferSizeInBytes, sessionOut, ShellContext.get().getOpPackageName(), 0L)).intValue();
        }

        AttributionSource attributionSource = ShellContext.get().getAttributionSource();
        Method asScopedParcelState = AttributionSource.class.getDeclaredMethod("asScopedParcelState");
        asScopedParcelState.setAccessible(true);
        try (AutoCloseable scopedState = (AutoCloseable) asScopedParcelState.invoke(attributionSource)) {
            Method getParcel = scopedState.getClass().getDeclaredMethod("getParcel");
            Parcel parcel = (Parcel) getParcel.invoke(scopedState);
            if (Build.VERSION.SDK_INT < 34) {
                Method nativeSetup = AudioRecord.class.getDeclaredMethod(
                    "native_setup", Object.class, Object.class, int[].class, int.class,
                    int.class, int.class, int.class, int[].class, Parcel.class, long.class, int.class);
                nativeSetup.setAccessible(true);
                return ((Integer) nativeSetup.invoke(
                    record, selfRef, attributes, sampleRateOut, channelMask, 0,
                    audioFormat, bufferSizeInBytes, sessionOut, parcel, 0L, 0)).intValue();
            }
            Method nativeSetup = AudioRecord.class.getDeclaredMethod(
                "native_setup", Object.class, Object.class, int[].class, int.class,
                int.class, int.class, int.class, int[].class, Parcel.class, long.class, int.class, int.class);
            nativeSetup.setAccessible(true);
            return ((Integer) nativeSetup.invoke(
                record, selfRef, attributes, sampleRateOut, channelMask, 0,
                audioFormat, bufferSizeInBytes, sessionOut, parcel, 0L, 0, 0)).intValue();
        }
    }

    private static void installConfigurationController() {
        try {
            Class<?> ccClass = Class.forName("android.app.ConfigurationController");
            Class<?> atiClass = Class.forName("android.app.ActivityThreadInternal");
            Constructor<?> ctor = ccClass.getDeclaredConstructor(atiClass);
            ctor.setAccessible(true);
            Object controller = ctor.newInstance(activityThread);
            Field field = activityThreadClass.getDeclaredField("mConfigurationController");
            field.setAccessible(true);
            field.set(activityThread, controller);
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: ConfigurationController init skipped: " + error);
        }
    }

    private static void installBoundApplication() {
        try {
            Class<?> appBindDataClass = Class.forName("android.app.ActivityThread$AppBindData");
            Constructor<?> ctor = appBindDataClass.getDeclaredConstructor();
            ctor.setAccessible(true);
            Object bindData = ctor.newInstance();

            ApplicationInfo info = new ApplicationInfo();
            info.packageName = ShellContext.PACKAGE_NAME;

            Field appInfoField = appBindDataClass.getDeclaredField("appInfo");
            appInfoField.setAccessible(true);
            appInfoField.set(bindData, info);

            Field boundField = activityThreadClass.getDeclaredField("mBoundApplication");
            boundField.setAccessible(true);
            boundField.set(activityThread, bindData);
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: AppBindData init skipped: " + error);
        }
    }

    private static void installInitialApplication(Context context) {
        try {
            Application app = Instrumentation.newApplication(Application.class, context);
            Field initialAppField = activityThreadClass.getDeclaredField("mInitialApplication");
            initialAppField.setAccessible(true);
            initialAppField.set(activityThread, app);
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: mInitialApplication init skipped: " + error);
        }
    }

    private static IInterface getActivityManager() throws ReflectiveOperationException {
        if (activityManager == null) {
            Class<?> nativeClass = Class.forName("android.app.ActivityManagerNative");
            Method getDefault = nativeClass.getDeclaredMethod("getDefault");
            activityManager = (IInterface) getDefault.invoke(null);
        }
        return activityManager;
    }

    private static Method getGetContentProviderExternalMethod(IInterface am) throws NoSuchMethodException {
        if (getContentProviderExternalMethod == null) {
            try {
                getContentProviderExternalMethod = am.getClass().getMethod(
                    "getContentProviderExternal", String.class, int.class, IBinder.class, String.class);
                getContentProviderExternalFourArgs = true;
            } catch (NoSuchMethodException fallback) {
                getContentProviderExternalMethod = am.getClass().getMethod(
                    "getContentProviderExternal", String.class, int.class, IBinder.class);
                getContentProviderExternalFourArgs = false;
            }
        }
        return getContentProviderExternalMethod;
    }

    private static void setField(Class<?> owner, Object target, String fieldName, Object value)
            throws ReflectiveOperationException {
        Field field = owner.getDeclaredField(fieldName);
        field.setAccessible(true);
        field.set(target, value);
    }
}
