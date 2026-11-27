package org.sonicroute.app

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.animation.ValueAnimator
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Rect
import android.os.Build
import android.os.Bundle
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.style.TextAppearanceSpan
import android.view.View
import android.view.ViewGroup
import android.view.ViewTreeObserver
import android.view.WindowInsets
import android.widget.Button
import android.widget.LinearLayout
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import org.sonicroute.core.AudioOutputKind
import org.sonicroute.core.Backend
import org.sonicroute.core.BackendStatus
import org.sonicroute.core.LanServicePhase
import org.sonicroute.core.LanStopReason
import org.sonicroute.core.OperationResult
import org.sonicroute.core.OwnedFocusPhase
import org.sonicroute.core.OwnedPlaybackState
import org.sonicroute.core.PermissionSnapshot
import org.sonicroute.platform.LocalAudioState
import org.sonicroute.platform.AppAppearance
import org.sonicroute.platform.SonicRouteRuntime
import org.sonicroute.platform.SonicRouteRuntimeSnapshot

/**
 * Pure UI observer & command submitter. Leaving this Activity (HOME, app switch, screen lock)
 * detaches UI callbacks and closes any unconfirmed pairing window, while an explicitly enabled
 * PC LAN connection continues running inside SonicRouteConnectionService.
 */
class MainActivity : Activity() {
    override fun attachBaseContext(newBase: Context) {
        AppAppearance.migrateLanguage(newBase)
        super.attachBaseContext(newBase)
        AppAppearance.activityOverride(newBase)?.let(::applyOverrideConfiguration)
    }
    private lateinit var runtime: SonicRouteRuntime
    private var lastSnapshot: SonicRouteRuntimeSnapshot? = null
    private var lastOutputSignature: String = ""
    private var lastPeersSignature: String = ""
    private var updatingSystemSlider = false
    private var updatingTrackSlider = false
    private var notificationPermissionPrompted = false
    private lateinit var rulesPage: VolumeRulesPage
    private lateinit var mediaPage: MediaPlayersPage
    private lateinit var capturePage: PlaybackSharingPage
    private lateinit var screenPage: ScreenSharingPage
    private lateinit var appearancePage: AppearancePage
    private lateinit var nativeRouteDialog: NativeAppRouteDialog
    private var restoreRouteDialog: Bundle? = null
    private var stoppedRouteDialog: Bundle? = null
    private lateinit var inviteClipboard: InvitationClipboard
    private lateinit var navigation: RadioGroup
    private var activePage = R.id.nav_audio
    private var offsets = Bundle()
    private var backendExpanded = false
    private var revealPermissions = false
    private var connectionDetailsExpanded = false
    private var pendingOffsetPage: Int? = null
    private var lastConsent: String? = null
    private var started = false

    private val uiObserver: (SonicRouteRuntimeSnapshot) -> Unit = { snap ->
        lastSnapshot = snap
        render(snap)
    }

    private lateinit var backendButtons: Map<Backend, Button>
    private lateinit var activeBackend: TextView
    private lateinit var lanStatus: TextView
    private lateinit var btnLanToggle: Button
    private lateinit var btnLanPair: Button
    private lateinit var btnLanCopyInvite: Button
    private lateinit var lanInviteText: TextView
    private lateinit var lanPeersStatus: TextView
    private lateinit var lanPeersContainer: LinearLayout
    private lateinit var outputsContainer: LinearLayout
    private lateinit var bluetoothStatus: TextView
    private lateinit var btnBtPermission: Button
    private lateinit var btnBtSettings: Button
    private lateinit var btnRefreshAudio: Button
    private lateinit var btnPlayToggle: Button
    private lateinit var btnMuteTrack: Button
    private lateinit var routedReadback: TextView
    private lateinit var systemVolumeLabel: TextView
    private lateinit var systemVolumeSlider: SeekBar
    private lateinit var trackGainLabel: TextView
    private lateinit var trackGainSlider: SeekBar
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        runtime = SonicRouteRuntime.getInstance(this)
        setContentView(R.layout.activity_main)
        if (Build.VERSION.SDK_INT >= 35) {
            findViewById<ViewGroup>(R.id.page).setOnApplyWindowInsetsListener { page, insets ->
                val safe = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                page.setPadding(safe.left, safe.top, safe.right, safe.bottom)
                insets
            }
        }

        activeBackend = findViewById(R.id.active_backend)
        lanStatus = findViewById(R.id.lan_status)
        btnLanToggle = findViewById(R.id.btn_lan_toggle)
        btnLanPair = findViewById(R.id.btn_lan_pair)
        btnLanCopyInvite = findViewById(R.id.btn_lan_copy_invite)
        lanInviteText = findViewById(R.id.lan_invite_text)
        lanPeersStatus = findViewById(R.id.lan_peers_status)
        lanPeersContainer = findViewById(R.id.lan_peers_container)
        outputsContainer = findViewById(R.id.outputs_container)
        bluetoothStatus = findViewById(R.id.bluetooth_status)
        btnBtPermission = findViewById(R.id.btn_bt_permission)
        btnBtSettings = findViewById(R.id.btn_bt_settings)
        btnRefreshAudio = findViewById(R.id.btn_refresh_audio)
        btnPlayToggle = findViewById(R.id.btn_play_toggle)
        btnMuteTrack = findViewById(R.id.btn_mute_track)
        routedReadback = findViewById(R.id.routed_readback)
        systemVolumeLabel = findViewById(R.id.system_volume_label)
        systemVolumeSlider = findViewById(R.id.system_volume_slider)
        trackGainLabel = findViewById(R.id.track_gain_label)
        trackGainSlider = findViewById(R.id.track_gain_slider)
        inviteClipboard = InvitationClipboard(applicationContext)
        rulesPage = VolumeRulesPage(this, runtime)
        mediaPage = MediaPlayersPage(this, runtime)
        capturePage = PlaybackSharingPage(this, runtime, savedInstanceState)
        screenPage = ScreenSharingPage(this, runtime, savedInstanceState)
        appearancePage = AppearancePage(this, runtime::refreshAppearance)
        runtime.refreshAppearance()
        nativeRouteDialog = NativeAppRouteDialog(this, runtime) {
            backendExpanded = true
            revealPermissions = true
            showDisclosure(R.id.backend_options, R.id.backend_list, true)
            navigation.check(R.id.nav_settings)
        }
        restoreRouteDialog = savedInstanceState?.getBundle("routePicker")
        findViewById<Button>(R.id.native_app_route).setOnClickListener { nativeRouteDialog.show() }
        navigation = findViewById(R.id.navigation)
        offsets = savedInstanceState?.getBundle("pageOffsets") ?: Bundle()
        backendExpanded = savedInstanceState?.getBoolean("backendExpanded") ?: false
        connectionDetailsExpanded = savedInstanceState?.getBoolean("connectionDetails") ?: false
        findViewById<View>(R.id.brand_header).visibility =
            if (resources.configuration.screenHeightDp in 1..410) View.GONE else View.VISIBLE
        findViewById<Button>(R.id.backend_options).setOnClickListener {
            backendExpanded = !backendExpanded
            showDisclosure(R.id.backend_options, R.id.backend_list, backendExpanded)
        }
        findViewById<Button>(R.id.pc_details_toggle).setOnClickListener {
            connectionDetailsExpanded = !connectionDetailsExpanded
            showDisclosure(R.id.pc_details_toggle, R.id.pc_details, connectionDetailsExpanded)
        }
        showDisclosure(R.id.backend_options, R.id.backend_list, backendExpanded)
        showDisclosure(R.id.pc_details_toggle, R.id.pc_details, connectionDetailsExpanded)
        notificationPermissionPrompted = savedInstanceState?.getBoolean("notificationsRequested") ?: false
        navigation.setOnCheckedChangeListener { _, id -> showPage(id) }
        navigation.check(savedInstanceState?.getInt("page", R.id.nav_audio) ?: R.id.nav_audio)
        findViewById<Switch>(R.id.owned_audio_focus).setOnClickListener {
            runtime.setOwnedFocusEnabled((it as Switch).isChecked)
        }
        findViewById<Switch>(R.id.media_share).setOnClickListener {
            runtime.setPublishedMediaEnabled((it as Switch).isChecked)
        }
        findViewById<Switch>(R.id.clipboard_share).setOnClickListener {
            runtime.setClipboardSharingEnabled((it as Switch).isChecked)
        }
        findViewById<Button>(R.id.settings_media_access).setOnClickListener { mediaPage.openSettings() }
        findViewById<Button>(R.id.select_shared_folder).setOnClickListener {
            val picker = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or
                Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION)
            runCatching { startActivityForResult(picker, REQ_TREE) }.onFailure {
                Toast.makeText(this, R.string.settings_unavailable, Toast.LENGTH_SHORT).show()
            }
        }
        findViewById<Button>(R.id.revoke_shared_folder).setOnClickListener { runtime.revokeSharedTree() }

        backendButtons = mapOf(
            Backend.STANDARD to findViewById(R.id.backend_standard),
            Backend.SHIZUKU_SHELL to findViewById(R.id.backend_shizuku_shell),
            Backend.SHIZUKU_ROOT to findViewById(R.id.backend_shizuku_root),
            Backend.DIRECT_ROOT to findViewById(R.id.backend_direct_root)
        )
        backendButtons.forEach { (backend, button) ->
            button.setOnClickListener { runtime.selectBackend(backend) }
        }

        btnLanToggle.setOnClickListener {
            val snap = runtime.currentSnapshot()
            if (snap.lifecycle.lanEnabled) {
                runtime.stopLanByUser()
            } else {
                maybeRequestNotificationPermission()
                runtime.startLanByUser(openPairing = false)
            }
        }
        btnLanPair.setOnClickListener {
            val snap = runtime.currentSnapshot()
            if (snap.lan.activeInvite != null) {
                runtime.closePairingWindow()
            } else {
                maybeRequestNotificationPermission()
                runtime.openPairingWindow()
            }
        }
        btnLanCopyInvite.setOnClickListener {
            val invite = runtime.currentSnapshot().lan.activeInvite ?: return@setOnClickListener
            val copied = inviteClipboard.copy(invite)
            if (!copied || Build.VERSION.SDK_INT < 33) {
                Toast.makeText(this, if (copied) R.string.copied_invite else R.string.copy_failed, Toast.LENGTH_SHORT).show()
            }
        }

        btnBtPermission.setOnClickListener {
            if (Build.VERSION.SDK_INT >= 31) {
                requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), REQ_BT_CONNECT)
            }
        }
        btnBtSettings.setOnClickListener {
            runCatching { startActivity(runtime.buildBluetoothSettingsIntent()) }.onFailure {
                Toast.makeText(this, R.string.settings_unavailable, Toast.LENGTH_SHORT).show()
            }
        }
        btnRefreshAudio.setOnClickListener { runtime.refreshForeground() }
        btnPlayToggle.setOnClickListener { runtime.toggleLocalTestTone() }
        btnMuteTrack.setOnClickListener { runtime.toggleOwnedTrackMute() }

        systemVolumeSlider.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            private var dragging = false
            override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                if (!fromUser || updatingSystemSlider) return
                if (dragging) findViewById<TextView>(R.id.volume_percent).text = getString(R.string.volume_percent, progress)
                else changeVolume(progress)
            }
            override fun onStartTrackingTouch(seekBar: SeekBar?) { dragging = true }
            override fun onStopTrackingTouch(seekBar: SeekBar) {
                dragging = false
                changeVolume(seekBar.progress)
            }
        })

        trackGainSlider.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                if (!fromUser || updatingTrackSlider) return
                runtime.setOwnedTrackGainPercent(progress)
            }

            override fun onStartTrackingTouch(seekBar: SeekBar?) = Unit
            override fun onStopTrackingTouch(seekBar: SeekBar?) = runtime.saveLocalPlaybackSettings()
        })

    }

    override fun onStart() {
        super.onStart()
        started = true
        lastOutputSignature = ""
        lastPeersSignature = ""
        runtime.attachUi(uiObserver)
        restoreRouteDialog?.let { if (activePage == R.id.nav_audio) nativeRouteDialog.show(it) }
        restoreRouteDialog = null
        stoppedRouteDialog = null
        runtime.setClipboardWindowFocused(hasWindowFocus(), window.decorView)
    }

    override fun onResume() {
        super.onResume()
        appearancePage.render()
        runtime.refreshForeground()
    }

    override fun onStop() {
        started = false
        if (isChangingConfigurations) stoppedRouteDialog = nativeRouteDialog.saveState()
        nativeRouteDialog.dismiss()
        capturePage.dismissPicker()
        runtime.setClipboardWindowFocused(false)
        inviteClipboard.leave(hasWindowFocus())
        runtime.detachUi(uiObserver, isChangingConfigurations)
        super.onStop()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        capturePage.save(outState)
        screenPage.save(outState)
        (nativeRouteDialog.saveState() ?: stoppedRouteDialog)?.let { outState.putBundle("routePicker", it) }
        outState.putInt("page", activePage)
        if (pendingOffsetPage != activePage)
            offsets.putInt(activePage.toString(), findViewById<ScrollView>(R.id.content_scroll).scrollY)
        outState.putBundle("pageOffsets", offsets)
        outState.putBoolean("backendExpanded", backendExpanded)
        outState.putBoolean("connectionDetails", connectionDetailsExpanded)
        outState.putBoolean("notificationsRequested", notificationPermissionPrompted)
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        capturePage.activityResult(requestCode, resultCode, data)
        screenPage.activityResult(requestCode, resultCode, data)
        if (requestCode == REQ_TREE && resultCode == RESULT_OK) data?.data?.let { runtime.selectSharedTree(it, data.flags) }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (::inviteClipboard.isInitialized) {
            if (hasFocus) inviteClipboard.sync(runtime.currentSnapshot().lan.activeInvite, true)
            runtime.setClipboardWindowFocused(started && hasFocus, window.decorView)
        }
    }

    private fun changeVolume(value: Int) {
        if (runtime.setSystemMediaVolumePercent(value) is OperationResult.Failure) {
            renderAudio(runtime.currentSnapshot().audio)
            Toast.makeText(this, R.string.feedback_volume, Toast.LENGTH_SHORT).show()
        }
    }

    private fun showDisclosure(button: Int, panel: Int, expanded: Boolean) {
        findViewById<View>(panel).visibility = if (expanded) View.VISIBLE else View.GONE
        findViewById<Button>(button).apply {
            setCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, if (expanded) R.drawable.ic_down else R.drawable.ic_next, 0)
            if (Build.VERSION.SDK_INT >= 30)
                stateDescription = getString(if (expanded) R.string.disclosure_expanded else R.string.disclosure_collapsed)
        }
    }

    private fun restorePageOffset(id: Int) {
        val scroll = findViewById<ScrollView>(R.id.content_scroll)
        pendingOffsetPage = id
        scroll.viewTreeObserver.addOnPreDrawListener(object : ViewTreeObserver.OnPreDrawListener {
            override fun onPreDraw(): Boolean {
                scroll.viewTreeObserver.removeOnPreDrawListener(this)
                if (activePage == id && pendingOffsetPage == id) {
                    if (id == R.id.nav_settings && revealPermissions) {
                        val section = findViewById<View>(R.id.backend_options)
                        val bounds = Rect()
                        section.getDrawingRect(bounds)
                        scroll.offsetDescendantRectToMyCoords(section, bounds)
                        scroll.scrollTo(0, bounds.top)
                        revealPermissions = false
                    } else scroll.scrollTo(0, offsets.getInt(id.toString(), 0))
                    pendingOffsetPage = null
                }
                return true
            }
        })
        scroll.invalidate()
    }

    private fun showPage(id: Int) {
        val pages = mapOf(R.id.nav_audio to R.id.panel_audio, R.id.nav_devices to R.id.panel_devices, R.id.nav_rules to R.id.panel_rules,
            R.id.nav_pc to R.id.panel_pc, R.id.nav_settings to R.id.panel_settings)
        if (id !in pages) return
        val previous = activePage
        if (started && previous != id && pendingOffsetPage != previous)
            offsets.putInt(previous.toString(), findViewById<ScrollView>(R.id.content_scroll).scrollY)
        activePage = id
        if (previous == R.id.nav_audio && previous != id) nativeRouteDialog.dismiss()
        if (previous == R.id.nav_pc && previous != id) {
            capturePage.dismissPicker()
            val lan = runtime.currentSnapshot().lan
            runtime.currentSnapshot().files.offer?.let { runtime.decideFileOffer(it.id, false) }
            lan.aliasBinding?.let { runtime.decideAliasBinding(it.confirmationId, false) }
            if (lan.activeInvite != null) runtime.closePairingWindow()
            inviteClipboard.leave(hasWindowFocus())
        }
        for ((nav, panel) in pages) {
            val view = findViewById<View>(panel)
            view.animate().cancel()
            view.alpha = 1f
            view.visibility = if (id == nav) View.VISIBLE else View.GONE
            if (nav == id && previous != id && started && ValueAnimator.areAnimatorsEnabled()) {
                view.alpha = .82f
                view.animate().alpha(1f).setDuration(130).start()
            }
        }
        runtime.setVisibleAudioPage(id == R.id.nav_audio, diagnostics = false, devices = id == R.id.nav_devices)
        if (started) render(runtime.currentSnapshot())
        restorePageOffset(id)
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        capturePage.permissionResult(requestCode, grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED)
        if (requestCode == REQ_BT_CONNECT) {
            val granted = grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED
            if (!granted) {
                bluetoothStatus.text = getString(R.string.bt_permission_denied)
            }
            runtime.refreshForeground()
        }
    }

    private fun maybeRequestNotificationPermission() {
        if (notificationPermissionPrompted) return
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermissionPrompted = true
            // Optional status notification permission; FGS still functions if denied.
            runCatching { requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQ_POST_NOTIF) }
        }
    }

    private fun render(snap: SonicRouteRuntimeSnapshot) {
        nativeRouteDialog.render(snap)
        val consent = snap.files.offer?.id ?: snap.lan.aliasBinding?.confirmationId ?: snap.lan.pendingPairing?.peerFingerprintSha256
        val newConsent = consent != null && consent != lastConsent
        lastConsent = consent
        if (started && newConsent) {
            offsets.putInt(R.id.nav_pc.toString(), 0)
            if (activePage != R.id.nav_pc) {
                navigation.check(R.id.nav_pc)
                return
            }
            restorePageOffset(R.id.nav_pc)
        }
        inviteClipboard.sync(snap.lan.activeInvite, hasWindowFocus())
        when (activePage) {
            R.id.nav_pc -> renderLan(snap)
            R.id.nav_audio -> { renderAudio(snap.audio); mediaPage.render(snap.media) }
            R.id.nav_devices -> renderAudio(snap.audio)
            R.id.nav_rules -> rulesPage.render(snap.volumeRules)
            R.id.nav_settings -> {
                findViewById<Switch>(R.id.owned_audio_focus).isChecked = snap.audio.focusEnabled
                findViewById<TextView>(R.id.owned_focus_status).text = if (snap.audio.focusEnabled) {
                    getString(R.string.owned_focus_tracks, focusLabel(snap.audio.localPlayback.focus), focusLabel(snap.audio.playback.focus))
                } else getString(R.string.focus_mix_summary)
                findViewById<Switch>(R.id.media_share).isChecked = snap.publishedMediaEnabled
                findViewById<Button>(R.id.settings_media_access).apply {
                    visibility = if (snap.publishedMediaEnabled) View.VISIBLE else View.GONE
                    setText(if (snap.media.accessGranted && !snap.media.unavailable) R.string.manage_media_access else R.string.media_grant)
                }
                findViewById<Switch>(R.id.clipboard_share).isChecked = snap.clipboardSharingEnabled
                val files = snap.files
                findViewById<Button>(R.id.select_shared_folder).setText(
                    if (files.hasSavedSelection) R.string.change_shared_folder else R.string.select_shared_folder)
                findViewById<TextView>(R.id.shared_folder_status).text = when {
                    files.selectionFailed && files.hasSavedSelection && files.folder == null -> getString(R.string.folder_unavailable)
                    files.selectionFailed -> getString(R.string.folder_failed, files.folder ?: getString(R.string.none_label))
                    files.folder == null -> getString(R.string.no_shared_folder)
                    else -> getString(if (files.writable) R.string.folder_active else R.string.folder_read_only, files.folder)
                }
                findViewById<Button>(R.id.revoke_shared_folder).visibility = if (files.hasSavedSelection) View.VISIBLE else View.GONE
                renderPermissions(snap.permissions)
            }
        }
    }

    private fun renderLan(snap: SonicRouteRuntimeSnapshot) {
        capturePage.render(snap)
        screenPage.render(snap)
        val state = snap.lan
        val offer = snap.files.offer
        val needsConsent = offer != null || state.pendingPairing != null || state.aliasBinding != null
        val consent = findViewById<View>(R.id.pc_consent) ?: if (needsConsent) {
            findViewById<android.view.ViewStub>(R.id.pc_consent_stub).inflate()
        } else null
        consent?.findViewById<View>(R.id.file_offer_box)?.visibility = if (offer != null) View.VISIBLE else View.GONE
        if (offer != null) {
            findViewById<TextView>(R.id.file_offer_text).text = getString(R.string.incoming_file, offer.peer,
                offer.name, android.text.format.Formatter.formatFileSize(this, offer.size), offer.folder, offer.fingerprint)
            findViewById<Button>(R.id.file_offer_accept).setOnClickListener { runtime.decideFileOffer(offer.id, true) }
            findViewById<Button>(R.id.file_offer_reject).setOnClickListener { runtime.decideFileOffer(offer.id, false) }
        }
        val lc = snap.lifecycle
        btnLanToggle.setText(if (lc.lanEnabled) R.string.action_lan_stop else R.string.action_lan_start)
        btnLanPair.setText(if (state.activeInvite != null) R.string.action_pair_close else R.string.action_pair_open)
        val firstPair = state.trustedPeers.isEmpty()
        btnLanToggle.isActivated = !firstPair && !lc.lanEnabled
        btnLanPair.isActivated = firstPair && state.activeInvite == null
        val actions = btnLanToggle.parent as ViewGroup
        val trailingAction = if (firstPair) btnLanToggle else btnLanPair
        if (actions.getChildAt(actions.childCount - 1) !== trailingAction) actions.bringChildToFront(trailingAction)

        val online = state.connectedPeers.distinctBy { it.fingerprintSha256 }.size
        val activeShare = snap.screenSharing != null || snap.playbackSharing != null
        val sharingDetails = online > 0 || activeShare
        findViewById<View>(R.id.pc_sharing_hint).visibility = if (sharingDetails) View.GONE else View.VISIBLE
        findViewById<View>(R.id.share_screen_status).visibility = if (sharingDetails) View.VISIBLE else View.GONE
        findViewById<View>(R.id.capture_playback_status).visibility = if (sharingDetails) View.VISIBLE else View.GONE
        val receiverState = when (lc.phase) {
            LanServicePhase.OFF -> getString(when (lc.lastStopReason) {
                LanStopReason.PROCESS_RECLAIMED -> R.string.pc_interrupted
                LanStopReason.USER_STOPPED -> R.string.pc_closed
                LanStopReason.FGS_START_DENIED -> R.string.pc_failed
                LanStopReason.NONE -> R.string.pc_off
            })
            LanServicePhase.WAITING_FOR_LAN -> getString(R.string.pc_wait_wifi)
            LanServicePhase.LISTENING_IDLE -> getString(R.string.pc_ready)
            LanServicePhase.LISTENING_PAIRING -> getString(R.string.pc_pairing)
            LanServicePhase.CONNECTED -> getString(R.string.pc_online, online)
        }
        val issue = state.lastError?.let { reason ->
            getString(when {
                reason.startsWith("nsd_") -> R.string.pc_error_discovery
                reason == "trust_store_write_failed" -> R.string.pc_error_trust
                reason == "network_callback_unavailable" -> R.string.pc_error_network
                else -> R.string.pc_error_receiver
            })
        }
        lanStatus.text = if (issue == null) receiverState else getString(R.string.pc_state_err, receiverState, issue)
        val unavailable = getString(R.string.none_label)
        val endpoint = state.localIp?.takeIf { state.port > 0 }?.let { "$it:${state.port}" } ?: unavailable
        val discovery = state.nsdServiceName?.takeIf { state.nsdRegistered } ?: getString(R.string.lan_nsd_off)
        findViewById<TextView>(R.id.pc_details).text = getString(R.string.pc_details_value,
            state.androidFingerprintSha256.ifEmpty { unavailable }, endpoint, discovery,
            getString(if (snap.foregroundServiceRunning) R.string.lan_fgs_active else R.string.lan_fgs_off))

        val invite = state.activeInvite
        if (invite != null) {
            btnLanCopyInvite.visibility = View.VISIBLE
            lanInviteText.visibility = View.VISIBLE
            lanInviteText.setText(R.string.lan_invite_display)
        } else {
            btnLanCopyInvite.visibility = View.GONE
            lanInviteText.visibility = View.GONE
        }

        val pending = state.pendingPairing
        if (pending != null) {
            consent?.findViewById<View>(R.id.lan_pending_box)?.visibility = View.VISIBLE
            findViewById<TextView>(R.id.lan_pending_text).text = getString(
                R.string.lan_pending_confirm,
                pending.displayName,
                pending.peerFingerprintSha256
            )
            fun decide(allow: Boolean) {
                if (runtime.currentSnapshot().lan.pendingPairing === pending) runtime.decidePendingPairing(allow)
            }
            findViewById<Button>(R.id.btn_pair_confirm).setOnClickListener { decide(true) }
            findViewById<Button>(R.id.btn_pair_reject).setOnClickListener { decide(false) }
        } else {
            consent?.findViewById<View>(R.id.lan_pending_box)?.visibility = View.GONE
        }
        val alias = state.aliasBinding
        consent?.findViewById<View>(R.id.alias_box)?.visibility = if (alias == null) View.GONE else View.VISIBLE
        if (alias != null) {
            findViewById<TextView>(R.id.alias_prompt).text = getString(
                if (alias.awaitingProof) R.string.alias_waiting else R.string.alias_confirm,
                alias.displayName, alias.peerFingerprintSha256)
            findViewById<Button>(R.id.alias_allow).apply {
                visibility = if (alias.awaitingProof) View.GONE else View.VISIBLE
                setOnClickListener { runtime.decideAliasBinding(alias.confirmationId, true) }
            }
            findViewById<Button>(R.id.alias_reject).apply {
                setText(if (alias.awaitingProof) android.R.string.cancel else R.string.action_pair_reject)
                setOnClickListener { runtime.decideAliasBinding(alias.confirmationId, false) }
            }
        }

        val connectedFps = state.connectedPeers.map { it.fingerprintSha256 }.toSet()
        lanPeersStatus.text = if (state.trustedPeers.isEmpty()) getString(R.string.pc_empty)
            else getString(R.string.pc_counts, state.trustedPeers.size, connectedFps.size)
        val peersSig = state.trustedPeers.joinToString { "${it.fingerprintSha256}:${it.displayName}:${it.fingerprintSha256 in connectedFps}" }
        if (peersSig == lastPeersSignature) return
        lastPeersSignature = peersSig
        lanPeersContainer.removeAllViews()
        val sameName = state.trustedPeers.groupBy { it.displayName.trim().lowercase() }
        for (peer in state.trustedPeers) {
            val onlineLabel = getString(if (peer.fingerprintSha256 in connectedFps) R.string.lan_peer_online else R.string.lan_peer_offline)
            val peersWithName = sameName.getValue(peer.displayName.trim().lowercase())
            val shortKey = if (peersWithName.size == 1) null else {
                val pin = peer.fingerprintSha256
                val distinguishingLength = peersWithName.filter { it !== peer }.maxOfOrNull {
                    pin.commonPrefixWith(it.fingerprintSha256, ignoreCase = true).length + 1
                } ?: 0
                pin.take(maxOf(8, distinguishingLength)).uppercase().chunked(4).joinToString(" ")
            }
            val deviceLabel = shortKey?.let { getString(R.string.pc_named_device, peer.displayName, it) } ?: peer.displayName
            val detail = shortKey?.let { getString(R.string.pc_peer_key, onlineLabel, it) } ?: onlineLabel
            lanPeersContainer.addView(rowButton().apply {
                text = SpannableStringBuilder(peer.displayName).append('\n').append(detail,
                    TextAppearanceSpan(this@MainActivity, R.style.SonicRoute_RowDetail), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                setCompoundDrawablesRelativeWithIntrinsicBounds(R.drawable.ic_pc, 0, R.drawable.ic_next, 0)
                setOnClickListener {
                    AlertDialog.Builder(this@MainActivity).setTitle(deviceLabel)
                        .setMessage(getString(R.string.pc_peer_detail, onlineLabel, peer.fingerprintSha256))
                        .setNegativeButton(android.R.string.cancel, null)
                        .setPositiveButton(R.string.action_revoke_peer) { _, _ ->
                            AlertDialog.Builder(this@MainActivity)
                                .setMessage(getString(R.string.revoke_confirmation, deviceLabel))
                                .setNegativeButton(android.R.string.cancel, null)
                                .setPositiveButton(R.string.action_revoke_peer) { _, _ -> runtime.revokePeer(peer.fingerprintSha256) }.show()
                        }.show()
                }
            })
        }
    }

    private fun renderPermissions(snapshot: PermissionSnapshot) {
        activeBackend.text = getString(
            R.string.backend_current,
            backendName(snapshot.active)
        )
        backendButtons.forEach { (backend, button) ->
            val reading = snapshot.backends.getValue(backend)
            val status = getString(statusLabel(reading.status))
            val detail = reading.uid?.let { getString(R.string.status_with_uid, status, it) } ?: status
            val inUse = snapshot.active == backend
            button.text = getString(if (inUse) R.string.backend_active else R.string.backend_row, backendName(backend), detail)
            button.isActivated = inUse
            button.setCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, if (inUse) R.drawable.ic_check else 0, 0)
            button.isEnabled = when (reading.status) {
                BackendStatus.VERIFYING, BackendStatus.INCOMPATIBLE -> false
                BackendStatus.UNAVAILABLE, BackendStatus.LOST, BackendStatus.UNCHECKED ->
                    backend == Backend.DIRECT_ROOT
                else -> true
            }
        }

    }

    private fun focusLabel(phase: OwnedFocusPhase) = getString(when (phase) {
        OwnedFocusPhase.INACTIVE -> R.string.owned_focus_idle
        OwnedFocusPhase.MIXING -> R.string.owned_focus_mix
        OwnedFocusPhase.HELD -> R.string.owned_focus_held
        OwnedFocusPhase.PAUSED -> R.string.owned_focus_paused
        OwnedFocusPhase.LOST -> R.string.owned_focus_lost
        OwnedFocusPhase.DENIED -> R.string.owned_focus_denied
    })

    private fun renderAudio(state: LocalAudioState) {
        val actualNames = state.outputs.filter { it.isDefault && it.kind != AudioOutputKind.CURRENT }
            .joinToString(" · ") { it.name }
        if (activePage == R.id.nav_audio) {
            findViewById<TextView>(R.id.system_volume_output).text = if (actualNames.isEmpty()) getString(R.string.system_volume_title)
                else getString(R.string.volume_with_output, actualNames)
            val vol = state.volume
            systemVolumeLabel.text = when {
                vol.isFixedVolume -> getString(R.string.system_volume_fixed)
                vol.dndRestricted -> getString(R.string.system_volume_dnd, vol.percent)
                else -> getString(R.string.volume_steps, vol.streamIndex, vol.maxIndex)
            }
            systemVolumeSlider.isEnabled = !vol.isFixedVolume
            if (!systemVolumeSlider.isPressed) {
                findViewById<TextView>(R.id.volume_percent).text = getString(R.string.volume_percent, vol.percent)
                updatingSystemSlider = true
                systemVolumeSlider.progress = vol.percent
                updatingSystemSlider = false
            }
            return
        }
        val signature = buildString {
            append(state.localPlayback.targetKey)
            append('|')
            for (o in state.outputs) {
                append(o.key).append(':').append(o.isDefault).append(':').append(o.name).append(';')
            }
        }
        if (signature != lastOutputSignature) {
            lastOutputSignature = signature
            outputsContainer.removeAllViews()
            for (sink in state.outputs) {
                val selected = sink.key.equals(state.localPlayback.targetKey, ignoreCase = true)
                val icon = when (sink.kind) {
                    AudioOutputKind.CURRENT -> R.drawable.ic_devices
                    AudioOutputKind.SPEAKER -> R.drawable.ic_speaker
                    AudioOutputKind.BLUETOOTH -> R.drawable.ic_bt
                    AudioOutputKind.WIRED, AudioOutputKind.USB -> R.drawable.ic_headphones
                    AudioOutputKind.OTHER -> R.drawable.ic_devices
                }
                val button = rowButton().apply {
                    isActivated = selected
                    isSelected = selected
                    setCompoundDrawablesRelativeWithIntrinsicBounds(icon, 0, if (selected) R.drawable.ic_check else 0, 0)
                    text = if (sink.isFallbackProfile) "${sink.name} · SCO" else sink.name
                    setOnClickListener { runtime.selectOutputTarget(sink.key) }
                }
                outputsContainer.addView(button)
            }
        }

        findViewById<TextView>(R.id.device_actual_output).text = if (actualNames.isEmpty()) getString(R.string.device_unknown)
            else getString(R.string.device_actual, actualNames)
        val bt = state.bluetooth
        btnBtPermission.visibility = if (bt.connectPermissionRequired && !bt.connectPermissionGranted) {
            View.VISIBLE
        } else {
            View.GONE
        }
        bluetoothStatus.text = when {
            !bt.supported -> getString(R.string.bt_unsupported)
            !bt.connectPermissionGranted -> getString(R.string.bt_needs_permission)
            !bt.adapterEnabled -> getString(R.string.bt_disabled)
            else -> {
                val connected = (bt.connectedA2dpNames + bt.connectedLeAudioNames).distinct()
                val connText = if (connected.isEmpty()) getString(R.string.none_label) else connected.joinToString(", ")
                val bondedText = if (bt.bondedAudioNames.isEmpty()) {
                    getString(R.string.none_label)
                } else {
                    bt.bondedAudioNames.take(4).joinToString(", ")
                }
                getString(R.string.bt_status_ready, connText, bt.bondedAudioNames.size, bondedText)
            }
        }

        val pb = state.localPlayback
        val target = state.outputs.firstOrNull { it.key.equals(pb.targetKey, true) }
        val targetName = target?.name ?: getString(R.string.output_offline)
        val isPlaying = pb.state == OwnedPlaybackState.PLAYING || pb.state == OwnedPlaybackState.STARTING
        btnPlayToggle.setText(if (isPlaying) R.string.action_stop_tone else R.string.action_play_tone)
        btnPlayToggle.isEnabled = isPlaying || target != null
        btnMuteTrack.setText(if (pb.trackMuted) R.string.action_unmute_track else R.string.action_mute_track)

        val readback = if (pb.focus == OwnedFocusPhase.PAUSED) focusLabel(pb.focus) else when (pb.state) {
            OwnedPlaybackState.IDLE -> when {
                pb.focus == OwnedFocusPhase.LOST || pb.focus == OwnedFocusPhase.DENIED -> focusLabel(pb.focus)
                target == null -> getString(R.string.output_offline)
                else -> null
            }
            OwnedPlaybackState.STARTING -> getString(R.string.routed_starting, targetName)
            OwnedPlaybackState.STOPPING -> getString(R.string.routed_stopping)
            OwnedPlaybackState.ERROR -> getString(
                R.string.routed_error,
                pb.error?.wireId ?: "error",
                pb.errorDetail ?: ""
            )
            OwnedPlaybackState.PLAYING -> {
                val rb = pb.readback
                if (rb == null) {
                    getString(R.string.routed_starting, targetName)
                } else {
                    val actualText = rb.actualSink?.name
                        ?: getString(R.string.none_label)
                    val verdict = if (rb.verified) getString(R.string.verdict_verified) else getString(R.string.verdict_mismatch)
                    getString(R.string.routed_playing, targetName, actualText, verdict)
                }
            }
        }
        routedReadback.text = readback
        routedReadback.visibility = if (readback == null) View.GONE else View.VISIBLE

        trackGainLabel.text = if (pb.trackMuted) {
            getString(R.string.track_gain_muted, pb.trackGainPercent)
        } else {
            getString(R.string.track_gain_label, pb.trackGainPercent)
        }
        updatingTrackSlider = true
        trackGainSlider.progress = pb.trackGainPercent
        updatingTrackSlider = false

    }

    private fun backendName(backend: Backend) = getString(
        when (backend) {
            Backend.STANDARD -> R.string.backend_standard
            Backend.SHIZUKU_SHELL -> R.string.backend_shizuku_shell
            Backend.SHIZUKU_ROOT -> R.string.backend_shizuku_root
            Backend.DIRECT_ROOT -> R.string.backend_direct_root
        }
    )

    private fun statusLabel(status: BackendStatus): Int = when (status) {
        BackendStatus.UNCHECKED -> R.string.state_unchecked
        BackendStatus.UNAVAILABLE -> R.string.state_unavailable
        BackendStatus.INCOMPATIBLE -> R.string.state_incompatible
        BackendStatus.NEEDS_AUTHORIZATION -> R.string.state_needs_auth
        BackendStatus.VERIFYING -> R.string.state_verifying
        BackendStatus.READY -> R.string.state_ready
        BackendStatus.DENIED -> R.string.state_denied
        BackendStatus.LOST -> R.string.state_lost
        BackendStatus.FAILED -> R.string.state_failed
        BackendStatus.TIMED_OUT -> R.string.state_timed_out
    }

    private companion object {
        const val REQ_BT_CONNECT = 1001
        const val REQ_POST_NOTIF = 1002
        const val REQ_TREE = 1003
    }
}
