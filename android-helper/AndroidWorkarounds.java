package org.sonicroute.android;

import android.app.Application;
import android.app.Instrumentation;
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
import android.os.Process;
import java.lang.ref.WeakReference;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.Arrays;

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

                try {
                    setField(activityThreadClass, activityThread, "mSystemThread", true);
                } catch (NoSuchFieldException ignored) {
                    // Some ROMs do not expose the system-thread flag.
                }
                setField(activityThreadClass, null, "sCurrentActivityThread", activityThread);
            }
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException("Unable to initialize ActivityThread for app_process", error);
        }

        // Initialize it before the system context reads the process configuration, when present.
        installConfigurationController();

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
        installInitialApplication(ShellContext.get());
        applied = true;
    }

    public static synchronized void applyForAudioCapture() {
        ensureThreadInitialized();
        // Legacy AudioPolicy sinks may construct AudioRecord without a Context.
        installBoundApplication();
        apply();
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
            int source, int sampleRate, int channels, int channelMask, int encoding,
            int bufferSizeInBytes, String mixAddress) throws Exception {
        Constructor<AudioRecord> ctor = AudioRecord.class.getDeclaredConstructor(long.class);
        ctor.setAccessible(true);
        AudioRecord record = ctor.newInstance(0L);
        boolean initialized = false;
        try {
            setField(AudioRecord.class, record, "mRecordingState", AudioRecord.RECORDSTATE_STOPPED);

            Looper looper = Looper.myLooper();
            if (looper == null) {
                looper = Looper.getMainLooper();
            }
            setField(AudioRecord.class, record, "mInitializationLooper", looper);

            AudioAttributes.Builder attrBuilder = new AudioAttributes.Builder();
            Method setPreset = AudioAttributes.Builder.class.getMethod("setInternalCapturePreset", int.class);
            setPreset.invoke(attrBuilder, source);
            if (mixAddress != null) {
                AudioAttributes.Builder.class.getMethod("addTag", String.class).invoke(attrBuilder, "addr=" + mixAddress);
                setField(AudioRecord.class, record, "mIsSubmixFullVolume", true);
            }
            AudioAttributes attributes = attrBuilder.build();
            setField(AudioRecord.class, record, "mAudioAttributes", attributes);

            Method paramCheck = AudioRecord.class.getDeclaredMethod("audioParamCheck", int.class, int.class, int.class);
            paramCheck.setAccessible(true);
            paramCheck.invoke(record, source, sampleRate, encoding);

            int[] sampleRateOut = new int[] { sampleRate };
            int[] sessionOut = new int[] { AudioManager.AUDIO_SESSION_ID_GENERATE };
            int initResult = invokeNativeSetup(record, attributes, sampleRateOut, channels, channelMask, encoding,
                    bufferSizeInBytes, sessionOut);
            if (initResult != AudioRecord.SUCCESS) {
                throw new IllegalStateException("AudioRecord native_setup failed with code " + initResult);
            }

            setField(AudioRecord.class, record, "mSampleRate", sampleRateOut[0]);
            setField(AudioRecord.class, record, "mSessionId", sessionOut[0]);
            setField(AudioRecord.class, record, "mState", AudioRecord.STATE_INITIALIZED);
            initialized = true;
            return record;
        } finally {
            if (!initialized) {
                releaseAudioRecord(record);
            }
        }
    }

    public static void releaseAudioRecord(AudioRecord record) {
        try {
            record.release(); // AudioRecord.release() stops an active recorder.
        } catch (RuntimeException | LinkageError error) {
            System.err.println("AndroidWorkarounds: AudioRecord.release failed: " + error);
        }
    }

    private static int invokeNativeSetup(
            AudioRecord record,
            AudioAttributes attributes,
            int[] sampleRateOut,
            int channels,
            int channelMask,
            int audioFormat,
            int bufferSizeInBytes,
            int[] sessionOut) throws Exception {
        // Android 17 packages the position and index masks in AudioFormat.ChannelMasks.
        Method nativeSetup = findNativeSetup(Object.class, Object.class, int[].class, Object.class, int.class,
                int.class, int[].class, Parcel.class, long.class, int.class, int.class);
        Object channelMasks = null;
        if (nativeSetup != null) {
            Field masks = AudioRecord.class.getDeclaredField("mChannelMasks");
            masks.setAccessible(true);
            Constructor<?> constructor = masks.getType().getDeclaredConstructor(int.class, int.class);
            constructor.setAccessible(true);
            channelMasks = constructor.newInstance(channelMask, 0);
            masks.set(record, channelMasks);
        } else {
            setField(AudioRecord.class, record, "mChannelCount", channels);
            setField(AudioRecord.class, record, "mChannelMask", channelMask);
            nativeSetup = findSplitMaskSetup(Parcel.class, 2);
        }
        // Framework buffer validation uses the recorder's channel configuration.
        Method buffCheck = AudioRecord.class.getDeclaredMethod("audioBuffSizeCheck", int.class);
        buffCheck.setAccessible(true);
        buffCheck.invoke(record, bufferSizeInBytes);
        if (nativeSetup == null) {
            nativeSetup = findSplitMaskSetup(Parcel.class, 1);
        }
        WeakReference<AudioRecord> self = new WeakReference<>(record);
        if (nativeSetup == null) {
            nativeSetup = findSplitMaskSetup(String.class, 1);
            if (nativeSetup == null) {
                nativeSetup = findSplitMaskSetup(String.class, 0);
            }
            if (nativeSetup == null) {
                throw new NoSuchMethodException("No compatible AudioRecord.native_setup signature");
            }
            return invokeSplitMaskSetup(nativeSetup, record, self, attributes, sampleRateOut, channelMask, audioFormat,
                    bufferSizeInBytes, sessionOut, ShellContext.PACKAGE_NAME);
        }
        Object attributionSource = ShellContext.get().getAttributionSource();
        Method asScopedParcelState = attributionSource.getClass().getDeclaredMethod("asScopedParcelState");
        asScopedParcelState.setAccessible(true);
        try (AutoCloseable scopedState = (AutoCloseable) asScopedParcelState.invoke(attributionSource)) {
            Method getParcel = scopedState.getClass().getDeclaredMethod("getParcel");
            getParcel.setAccessible(true);
            Object parcel = getParcel.invoke(scopedState);
            if (channelMasks != null) {
                return (int) nativeSetup.invoke(record, self, attributes, sampleRateOut, channelMasks, audioFormat,
                        bufferSizeInBytes, sessionOut, parcel, 0L, 0, 0);
            }
            return invokeSplitMaskSetup(nativeSetup, record, self, attributes, sampleRateOut, channelMask, audioFormat,
                    bufferSizeInBytes, sessionOut, parcel);
        }
    }

    private static Method findSplitMaskSetup(Class<?> attributionType, int extraInts) {
        Class<?>[] signature = { Object.class, Object.class, int[].class, int.class, int.class, int.class,
                int.class, int[].class, attributionType, long.class, int.class, int.class };
        return findNativeSetup(Arrays.copyOf(signature, 10 + extraInts));
    }

    private static Method findNativeSetup(Class<?>... signature) {
        try {
            Method method = AudioRecord.class.getDeclaredMethod("native_setup", signature);
            method.setAccessible(true);
            return method;
        } catch (NoSuchMethodException ignored) {
            return null;
        }
    }

    private static int invokeSplitMaskSetup(Method method, AudioRecord record, WeakReference<AudioRecord> self,
            AudioAttributes attributes, int[] sampleRateOut, int channelMask, int audioFormat, int bufferSizeInBytes,
            int[] sessionOut, Object attribution) throws ReflectiveOperationException {
        Object[] args = { self, attributes, sampleRateOut, channelMask, 0, audioFormat,
                bufferSizeInBytes, sessionOut, attribution, 0L, 0, 0 };
        return (int) method.invoke(record, Arrays.copyOf(args, method.getParameterCount()));
    }

    private static void installConfigurationController() {
        try {
            Field field = activityThreadClass.getDeclaredField("mConfigurationController");
            field.setAccessible(true);
            if (field.get(activityThread) != null) {
                return;
            }
            Class<?> atiClass = Class.forName("android.app.ActivityThreadInternal");
            Constructor<?> ctor = field.getType().getDeclaredConstructor(atiClass);
            ctor.setAccessible(true);
            field.set(activityThread, ctor.newInstance(activityThread));
        } catch (NoSuchFieldException ignored) {
            // Android 11 has no ConfigurationController.
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: ConfigurationController init skipped: " + error);
        }
    }

    private static void installBoundApplication() {
        try {
            Field boundField = activityThreadClass.getDeclaredField("mBoundApplication");
            boundField.setAccessible(true);
            if (boundField.get(activityThread) != null) {
                return;
            }
            Class<?> appBindDataClass = boundField.getType();
            Constructor<?> ctor = appBindDataClass.getDeclaredConstructor();
            ctor.setAccessible(true);
            Object bindData = ctor.newInstance();

            ApplicationInfo info = new ApplicationInfo();
            info.packageName = ShellContext.PACKAGE_NAME;
            info.uid = Process.myUid();

            Field appInfoField = appBindDataClass.getDeclaredField("appInfo");
            appInfoField.setAccessible(true);
            appInfoField.set(bindData, info);

            boundField.set(activityThread, bindData);
        } catch (Throwable error) {
            System.err.println("AndroidWorkarounds: AppBindData init skipped: " + error);
        }
    }

    private static void installInitialApplication(Context context) {
        try {
            Field initialAppField = activityThreadClass.getDeclaredField("mInitialApplication");
            initialAppField.setAccessible(true);
            if (initialAppField.get(activityThread) == null) {
                initialAppField.set(activityThread, Instrumentation.newApplication(Application.class, context));
            }
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
