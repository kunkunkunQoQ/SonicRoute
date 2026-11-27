package org.sonicroute.app

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PersistableBundle
import org.sonicroute.core.SrapPairingInvite
import java.util.UUID

internal class InvitationClipboard(context: Context) {
    private val clipboard = context.getSystemService(ClipboardManager::class.java)
    private val prefs = context.getSharedPreferences("invitation_clip_owner", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private var expiry: Runnable? = null
    private var lastInvite: SrapPairingInvite? = null

    fun copy(invite: SrapPairingInvite): Boolean {
        val remaining = invite.expiresAtMs - System.currentTimeMillis()
        if (remaining <= 0) return false
        val marker = UUID.randomUUID().toString()
        val clip = ClipData.newPlainText("SonicRoute", invite.toCopyableText())
        clip.description.extras = PersistableBundle().apply {
            val sensitive = if (Build.VERSION.SDK_INT >= 33) ClipDescription.EXTRA_IS_SENSITIVE
                else "android.content.extra.IS_SENSITIVE"
            putBoolean(sensitive, true)
            putString(OWNER, marker)
        }
        if (runCatching { clipboard.setPrimaryClip(clip) }.isFailure) return false
        expiry?.let(main::removeCallbacks)
        lastInvite = invite
        prefs.edit().putString("marker", marker).putLong("deadline", invite.expiresAtMs).apply()
        expiry = Runnable { clearOwned(marker) }.also { main.postDelayed(it, remaining) }
        return true
    }

    fun sync(current: SrapPairingInvite?, focused: Boolean) {
        val marker = prefs.getString("marker", null) ?: return
        val valid = current != null && lastInvite?.inviteId?.contentEquals(current.inviteId) == true &&
            prefs.getLong("deadline", 0) > System.currentTimeMillis()
        if (!valid && focused) clearOwned(marker)
    }

    fun leave(focused: Boolean) {
        lastInvite = null
        expiry?.let(main::removeCallbacks)
        expiry = null
        sync(null, focused)
    }

    private fun clearOwned(marker: String) {
        if (prefs.getString("marker", null) != marker) return
        val description = runCatching { clipboard.primaryClipDescription }.getOrNull() ?: return
        if (description.extras?.getString(OWNER) == marker &&
            runCatching { clipboard.clearPrimaryClip() }.isFailure) return
        prefs.edit().remove("marker").remove("deadline").apply()
    }

    private companion object { const val OWNER = "org.sonicroute.clip.owner" }
}
