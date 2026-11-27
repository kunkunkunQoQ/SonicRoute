package org.sonicroute.platform

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.view.Surface
import org.sonicroute.core.*
import java.nio.ByteBuffer

data class SharedScreenState(val peer: String, val connected: Boolean)

/** One Android consent result, one destination session and at most one virtual display. */
class SharedScreenProjection internal constructor(private val context: Context, private val changed: () -> Unit) {
    private class Grant(val session: String, val peer: String, val token: MediaProjection, val width: Int, val height: Int, val dpi: Int) {
        lateinit var callback: MediaProjection.Callback
        var encoder: ScreenEncoder? = null
    }
    private val main = Handler(Looper.getMainLooper())
    @Volatile private var grant: Grant? = null
    private var generation = 0L
    private val expire = Runnable { if (grant?.encoder == null) stop() }
    val state get() = grant?.let { SharedScreenState(it.peer, it.encoder != null) }
    fun granted(session: String, peer: String) = grant?.let { it.session == session && it.peer == peer } == true
    fun capability() = if (grant != null) CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "one_projection",
        limits = mapOf("codec" to "h264", "maxUnit" to ScreenFrames.MAX_UNIT.toString(), "grant" to generation.toString()))
    else CapabilityEntry(CapabilityStatus.NEEDS_AUTHORIZATION, reason = "local_screen_consent", scope = "one_projection")

    fun begin(session: String, peer: String, result: Int, data: Intent, width: Int, height: Int, dpi: Int): Boolean {
        if (grant != null || result != Activity.RESULT_OK || width <= 0 || height <= 0 || dpi <= 0 ||
            !SonicRouteConnectionService.promoteProjection(this)) return false
        var projection: MediaProjection? = null
        return try {
            val token = context.getSystemService(MediaProjectionManager::class.java).getMediaProjection(result, data)
                ?: throw IllegalStateException("projection_denied")
            projection = token
            val scale = minOf(1.0, 1280.0 / maxOf(width, height), 720.0 / minOf(width, height))
            fun dimension(value: Int) = ((value * scale).toInt() / 16 * 16).coerceAtLeast(16)
            val g = Grant(session, peer, token, dimension(width), dimension(height), dpi)
            g.callback = object : MediaProjection.Callback() {
                override fun onStop() { if (grant === g) stop() }
            }
            token.registerCallback(g.callback, main)
            generation++
            grant = g
            main.postDelayed(expire, 60_000)
            changed()
            true
        } catch (_: RuntimeException) {
            grant = null
            runCatching { projection?.stop() }
            SonicRouteConnectionService.endProjection(this)
            false
        }
    }

    fun open(session: String, peer: String): OperationResult<ScreenFrames> {
        val g = grant?.takeIf { it.session == session && it.peer == peer } ?: return OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED)
        if (g.encoder != null) return OperationResult.Failure(ProtocolError.BUSY)
        val frames = ScreenFrames(g.width, g.height) { main.post { if (grant === g) stop() } }
        return try {
            val enc = ScreenEncoder(g.token, g.dpi, frames)
            g.encoder = enc
            enc.start()
            main.removeCallbacks(expire)
            changed()
            OperationResult.Done(frames)
        } catch (_: RuntimeException) {
            stop()
            OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "screen_encoder_unavailable")
        }
    }

    fun checkSession(isCurrent: (String) -> Boolean) { grant?.let { if (!isCurrent(it.session)) stop() } }
    fun endSession(session: String) { if (grant?.session == session) stop() }
    fun stop() {
        val g = grant ?: return
        grant = null
        generation++
        main.removeCallbacks(expire)
        g.encoder?.close()
        runCatching { g.token.unregisterCallback(g.callback) }
        runCatching { g.token.stop() }
        SonicRouteConnectionService.endProjection(this)
        changed()
    }
}

private class ScreenEncoder(private val token: MediaProjection, private val dpi: Int, private val frames: ScreenFrames) {
    private val thread = HandlerThread("SonicRoute screen encoder")
    private lateinit var handler: Handler
    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    private var display: VirtualDisplay? = null
    @Volatile private var closed = false
    private var formatConfig: ByteArray? = null

    fun start() {
        thread.start()
        handler = Handler(thread.looper)
        handler.post {
            if (closed) return@post
            try {
                val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, frames.width, frames.height).apply {
                    setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                    setInteger(MediaFormat.KEY_BIT_RATE, 2_000_000)
                    setInteger(MediaFormat.KEY_FRAME_RATE, 20)
                    setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER, 20f)
                    setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
                }
                val encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
                codec = encoder
                encoder.setCallback(callback, handler)
                encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                surface = encoder.createInputSurface()
                encoder.start()
                if (closed) return@post
                display = token.createVirtualDisplay("SonicRoute", frames.width, frames.height, dpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, surface, null, handler)
                    ?: throw IllegalStateException("display_unavailable")
            } catch (_: Exception) { frames.close() }
        }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) = Unit
        override fun onError(codec: MediaCodec, error: MediaCodec.CodecException) { frames.close() }
        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            if (closed) return
            try {
                val items = listOfNotNull(format.getByteBuffer("csd-0"), format.getByteBuffer("csd-1")).map { AnnexB.from(it.duplicate(), true) }
                if (items.isNotEmpty()) {
                    val bytes = items.reduce(ByteArray::plus)
                    require(bytes.size <= 65536)
                    formatConfig = bytes
                    frames.offer(ScreenUnit(bytes, 0, config = true, key = false))
                }
            } catch (_: RuntimeException) { frames.close() }
        }
        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (closed) return
            try {
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) { frames.close(); return }
                if (info.size == 0) return
                require(info.size in 1..ScreenFrames.MAX_UNIT && info.flags and MediaCodec.BUFFER_FLAG_PARTIAL_FRAME == 0)
                val config = info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
                if (config && formatConfig != null) return
                val buf = requireNotNull(codec.getOutputBuffer(index)).duplicate()
                buf.position(info.offset)
                buf.limit(info.offset + info.size)
                val bytes = AnnexB.from(buf, config)
                val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                if (frames.offer(ScreenUnit(bytes, info.presentationTimeUs.coerceAtLeast(0L), config, key))) {
                    codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
                }
            } catch (_: RuntimeException) { frames.close() }
            finally { runCatching { codec.releaseOutputBuffer(index, false) } }
        }
    }

    fun close() {
        closed = true
        frames.close()
        if (!::handler.isInitialized) return
        handler.post {
            runCatching { display?.release() }
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
            runCatching { surface?.release() }
            thread.quitSafely()
        }
    }
}

private object AnnexB {
    fun from(buffer: ByteBuffer, config: Boolean): ByteArray {
        require(buffer.remaining() in 1..if (config) 65536 else ScreenFrames.MAX_UNIT)
        val bytes = ByteArray(buffer.remaining()).also { buffer.get(it) }
        if (bytes.size >= 4 && bytes[0] == 0.toByte() && bytes[1] == 0.toByte() &&
            (bytes[2] == 1.toByte() || bytes[2] == 0.toByte() && bytes[3] == 1.toByte())) return bytes
        if (config && bytes[0].toInt() and 31 in 7..8) return byteArrayOf(0,0,0,1) + bytes
        val src = ByteBuffer.wrap(bytes)
        while (src.hasRemaining()) {
            require(src.remaining() >= 5)
            val pos = src.position()
            val size = src.int
            require(size in 1..src.remaining())
            src.putInt(pos, 1)
            src.position(src.position() + size)
        }
        return bytes
    }
}
