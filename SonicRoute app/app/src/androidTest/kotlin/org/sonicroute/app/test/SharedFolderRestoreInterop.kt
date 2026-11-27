package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.content.Context
import android.net.Uri
import android.os.Bundle
import android.os.Process
import android.os.SystemClock
import android.provider.DocumentsContract
import android.view.View
import android.widget.RadioGroup
import org.sonicroute.app.R
import org.sonicroute.platform.SonicRouteRuntime
import java.security.MessageDigest

/** Read back only the selected root and the system grant. The receipt contains no URI or file name. */
internal class SharedFolderRestoreInterop(private val instrument: Instrumentation, private val phase: String?) {
    private fun <T> ui(action: () -> T): T = instrument.onMain(action)

    fun run() {
        val context = instrument.targetContext
        val baseline = context.getSharedPreferences("interop_saf_restore_baseline", Context.MODE_PRIVATE)
        val result = Bundle()
        val pages = InteropActivity(instrument)
        var step = "phase"
        var code = Activity.RESULT_CANCELED
        try {
            require(phase in listOf("record", "cold", "updated"))
            step = "host-launch"
            val first = pages.launch()
            step = "persisted-grant"
            val selection = requireNotNull(context.getSharedPreferences("shared_saf_tree", Context.MODE_PRIVATE).getString("tree", null))
            val uri = Uri.parse(selection)
            val permission = context.contentResolver.persistedUriPermissions.single { it.uri == uri && it.isReadPermission }
            val root = DocumentsContract.buildDocumentUriUsingTree(uri, DocumentsContract.getTreeDocumentId(uri))
            val name = context.contentResolver.query(root, arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME),
                null, null, null)?.use { if (it.moveToFirst()) it.getString(0) else null }
            require(!name.isNullOrEmpty())
            val digest = MessageDigest.getInstance("SHA-256").digest(selection.toByteArray()).joinToString("") {
                "%02x".format(java.util.Locale.ROOT, it)
            }
            val installed = context.packageManager.getPackageInfo(context.packageName, 0).lastUpdateTime
            val processStart = Process.getStartElapsedRealtime()
            if (phase != "record") {
                step = "prior-grant-and-process"
                check(baseline.getString("grant", null) == digest && baseline.getBoolean("write", !permission.isWritePermission) == permission.isWritePermission)
                check(baseline.getLong("processStart", -1) != processStart)
                if (phase == "updated") check(installed > baseline.getLong("installed", Long.MAX_VALUE))
            }
            step = "runtime-restore"
            val runtime = ui { SonicRouteRuntime.getInstance(context) }
            val deadline = SystemClock.elapsedRealtime() + 12000
            while (!ui { runtime.currentSnapshot().files.folder == name } && SystemClock.elapsedRealtime() < deadline) SystemClock.sleep(80)
            val writable = ui {
                first.findViewById<RadioGroup>(R.id.navigation).check(R.id.nav_settings)
                val files = runtime.currentSnapshot().files
                check(files.folder == name && files.hasSavedSelection && !files.selectionFailed)
                check(first.findViewById<View>(R.id.revoke_shared_folder).isShown)
                files.writable
            }
            if (phase != "record") check(baseline.getBoolean("writable", !writable) == writable)
            step = "activity-recreation"
            val next = pages.recreate(first)
            ui {
                val files = runtime.currentSnapshot().files
                check(next !== first && next.findViewById<RadioGroup>(R.id.navigation).checkedRadioButtonId == R.id.nav_settings)
                check(files.folder == name && files.hasSavedSelection && files.writable == writable && !files.selectionFailed)
                check(next.findViewById<View>(R.id.revoke_shared_folder).isShown)
            }
            step = "receipt"
            if (phase == "record") check(baseline.edit().putString("grant", digest).putBoolean("write", permission.isWritePermission)
                .putBoolean("writable", writable).putLong("installed", installed).putLong("processStart", processStart).commit())
            if (phase == "updated") check(baseline.edit().clear().commit())
            result.putString("saf_restore", "passed-$phase")
            code = Activity.RESULT_OK
        } catch (e: Exception) {
            result.putString("saf_restore", "failed-$step-${e.javaClass.simpleName}")
        } finally {
            pages.close()
            instrument.finish(code, result)
        }
    }
}
