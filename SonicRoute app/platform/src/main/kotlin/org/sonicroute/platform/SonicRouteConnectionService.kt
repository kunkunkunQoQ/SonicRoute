package org.sonicroute.platform

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import org.sonicroute.core.LanServicePhase

/**
 * Foreground Service active ONLY while the user explicitly enabled PC LAN connection.
 * Uses START_NOT_STICKY (never resurrects after explicit stop or OS reclaim), holds zero
 * wake locks, exposes an explicit Stop action in the status notification, and binds
 * connectedDevice (plus mediaPlayback only when audio playback is active).
 */
class SonicRouteConnectionService : Service() {
    private var projectionOwner: Any? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        ensureChannel()
        activeInstance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val runtime = SonicRouteRuntime.getInstance(this)
        when (intent?.action) {
            ACTION_STOP_BY_USER -> {
                runtime.stopLanByUser()
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
                return START_NOT_STICKY
            }
            else -> {
                val snap = runtime.currentSnapshot()
                if (!snap.lifecycle.shouldRunForegroundService) {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                    return START_NOT_STICKY
                }
                val notification = buildStatusNotification(this, snap)
                val fgsType = computeFgsType(snap.lifecycle.shouldIncludeMediaPlaybackFgsType)
                try {
                    if (Build.VERSION.SDK_INT >= 29) {
                        startForeground(NOTIFICATION_ID, notification, fgsType)
                    } else {
                        startForeground(NOTIFICATION_ID, notification)
                    }
                    runtime.onForegroundServiceActive(true)
                } catch (t: Throwable) {
                    runtime.onForegroundServiceFailed("${t.javaClass.simpleName}:${t.message.orEmpty()}")
                    stopSelf()
                }
                return START_NOT_STICKY
            }
        }
    }

    override fun onDestroy() {
        if (activeInstance === this) {
            activeInstance = null
            val runtime = SonicRouteRuntime.getInstance(this)
            projectionOwner?.let(runtime::onProjectionServiceStopped)
            projectionOwner = null
            runtime.onForegroundServiceActive(false)
        }
        super.onDestroy()
    }

    internal fun applyForegroundTypeUpdate(snap: SonicRouteRuntimeSnapshot) {
        if (!snap.lifecycle.shouldRunForegroundService) return
        val notification = buildStatusNotification(this, snap)
        val wantMedia = snap.lifecycle.shouldIncludeMediaPlaybackFgsType
        if (Build.VERSION.SDK_INT >= 29) {
            val primaryType = computeFgsType(wantMedia)
            val promoted = runCatching {
                startForeground(NOTIFICATION_ID, notification, primaryType)
                true
            }.getOrDefault(false)
            if (!promoted) {
                // Android 14-16 may disallow adding a new FGS type while in background; stay safely
                // in connectedDevice FGS and keep the active LAN session uninterrupted.
                if (projectionOwner == null) runCatching {
                    startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
                }
            }
        } else {
            runCatching { startForeground(NOTIFICATION_ID, notification) }
        }
    }

    private fun computeFgsType(includeMediaPlayback: Boolean): Int {
        var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        if (projectionOwner != null) type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
        if (includeMediaPlayback) {
            type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
        }
        return type
    }

    private fun ensureChannel() {
        val mgr = getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        val existing = mgr.getNotificationChannel(CHANNEL_ID)
        val words = AppAppearance.forStrings(this)
        val name = words.getString(R.string.connection_channel)
        val summary = words.getString(R.string.connection_channel_summary)
        if (existing?.name == name && existing.description == summary) return
        val channel = existing ?: NotificationChannel(CHANNEL_ID, name, NotificationManager.IMPORTANCE_LOW).apply {
            setShowBadge(false)
            setSound(null, null)
            enableVibration(false)
        }
        channel.name = name
        channel.description = summary
        mgr.createNotificationChannel(channel)
    }

    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        SonicRouteRuntime.getInstance(this).refreshAppearance()
    }

    companion object {
        const val CHANNEL_ID = "sonicroute_lan_connection"
        const val NOTIFICATION_ID = 2001
        const val ACTION_START_OR_UPDATE = "org.sonicroute.platform.action.START_OR_UPDATE"
        const val ACTION_STOP_BY_USER = "org.sonicroute.platform.action.STOP_BY_USER"
        private const val REQ_OPEN_UI = 100
        private const val REQ_STOP_ACTION = 101
        @Volatile private var activeInstance: SonicRouteConnectionService? = null

        fun promoteProjection(owner: Any): Boolean {
            check(android.os.Looper.myLooper() == android.os.Looper.getMainLooper())
            val service = activeInstance ?: return false
            if (service.projectionOwner != null) return false
            val snap = SonicRouteRuntime.getInstance(service).currentSnapshot()
            if (!snap.lifecycle.shouldRunForegroundService || snap.lan.connectedPeers.isEmpty()) return false
            service.projectionOwner = owner
            return try {
                service.startForeground(NOTIFICATION_ID, buildStatusNotification(service, snap),
                    service.computeFgsType(snap.lifecycle.shouldIncludeMediaPlaybackFgsType))
                true
            } catch (_: RuntimeException) { service.projectionOwner = null; false }
        }

        fun endProjection(owner: Any) {
            check(android.os.Looper.myLooper() == android.os.Looper.getMainLooper())
            val service = activeInstance ?: return
            if (service.projectionOwner !== owner) return
            service.projectionOwner = null
            service.applyForegroundTypeUpdate(SonicRouteRuntime.getInstance(service).currentSnapshot())
        }

        fun startOrUpdate(context: Context): Boolean {
            val intent = Intent(context.applicationContext, SonicRouteConnectionService::class.java)
                .setAction(ACTION_START_OR_UPDATE)
            return runCatching {
                context.applicationContext.startForegroundService(intent)
                true
            }.getOrDefault(false)
        }

        fun updateForegroundTypeIfRunning(context: Context) {
            val runtime = SonicRouteRuntime.getInstance(context)
            if (!runtime.isForegroundServiceRunning()) return
            val svc = activeInstance
            if (svc != null) {
                svc.applyForegroundTypeUpdate(runtime.currentSnapshot())
            } else {
                updateIfRunning(context)
            }
        }

        fun updateIfRunning(context: Context) {
            val runtime = SonicRouteRuntime.getInstance(context)
            if (!runtime.isForegroundServiceRunning()) return
            val mgr = context.applicationContext.getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager
                ?: return
            runCatching {
                mgr.notify(NOTIFICATION_ID, buildStatusNotification(context.applicationContext, runtime.currentSnapshot()))
            }
        }

        fun refreshLanguageIfRunning(context: Context) {
            val service = activeInstance ?: return
            service.ensureChannel()
            updateIfRunning(context)
        }

        fun stop(context: Context) {
            val intent = Intent(context.applicationContext, SonicRouteConnectionService::class.java)
            runCatching { context.applicationContext.stopService(intent) }
        }

        internal fun buildStatusNotification(context: Context, snap: SonicRouteRuntimeSnapshot): Notification {
            val lan = snap.lan
            val lc = snap.lifecycle
            val words = AppAppearance.forStrings(context)
            val title = words.getString(R.string.connection_title)
            val text = when (lc.phase) {
                LanServicePhase.CONNECTED -> words.getString(R.string.connection_connected, lan.connectedPeers.joinToString(", ") { it.displayName })
                LanServicePhase.WAITING_FOR_LAN -> words.getString(R.string.connection_waiting, lan.trustedPeers.size)
                LanServicePhase.LISTENING_PAIRING -> words.getString(R.string.connection_pairing, "${lan.localIp ?: "LAN"}:${lan.port}")
                LanServicePhase.LISTENING_IDLE -> words.getString(R.string.connection_listening, "${lan.localIp ?: "LAN"}:${lan.port}")
                LanServicePhase.OFF -> words.getString(R.string.connection_stopped)
            }

            val launchIntent = context.packageManager.getLaunchIntentForPackage(context.packageName)?.apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            }
            val contentPi = launchIntent?.let {
                PendingIntent.getActivity(
                    context,
                    REQ_OPEN_UI,
                    it,
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
                )
            }

            val stopIntent = Intent(context, SonicRouteConnectionService::class.java).setAction(ACTION_STOP_BY_USER)
            val stopPi = PendingIntent.getService(
                context,
                REQ_STOP_ACTION,
                stopIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )

            val stopLabel = words.getString(R.string.connection_stop)
            val iconRes = context.applicationInfo.icon.takeIf { it != 0 } ?: android.R.drawable.stat_sys_data_bluetooth

            return Notification.Builder(context, CHANNEL_ID)
                .setSmallIcon(iconRes)
                .setContentTitle(title)
                .setContentText(text)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .apply { if (contentPi != null) setContentIntent(contentPi) }
                .addAction(Notification.Action.Builder(null, stopLabel, stopPi).build())
                .build()
        }
    }
}
