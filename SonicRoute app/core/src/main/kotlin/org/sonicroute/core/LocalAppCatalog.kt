package org.sonicroute.core

/** Worker-confined index of launcher labels. Only strings, UIDs and resource identifiers survive a scan. */
class LocalAppCatalog {
    companion object {
        fun isNeeded(trustedSessions: Int, uiAttached: Boolean, pickerOpen: Boolean, diagnosticsOpen: Boolean) =
            trustedSessions > 0 || uiAttached && (pickerOpen || diagnosticsOpen)
    }

    data class Source(val packageName: String, val uid: Int, val path: String, val component: String,
                      val resource: String, val loadLabel: () -> String)
    data class Changes(val sequence: Int, val packages: Set<String>)
    private data class Label(val uid: Int, val path: String, val component: String, val resource: String, val value: String)
    private var labels = emptyMap<String, Label>()
    private var locale: String? = null
    private var catalog: AppCatalogSnapshot? = null
    var sequence = 0
        private set

    fun read(language: String, self: String, changes: Changes?, changeQueryFailed: Boolean, forceScan: Boolean,
             changedNames: Set<String>, canceled: () -> Boolean, sources: () -> Sequence<Source>): AppCatalogSnapshot? {
        if (canceled()) return null
        if (catalog != null && locale == language && changes == null && !changeQueryFailed && !forceScan && changedNames.isEmpty()) return catalog
        val invalid = changedNames + changes?.packages.orEmpty()
        val canReuse = locale == language && !changeQueryFailed && !(forceScan && changedNames.isEmpty())
        val next = LinkedHashMap<String, Label>()
        val entries = ArrayList<AppCatalogEntry>()
        for (source in sources()) {
            if (canceled()) return null
            val pkg = source.packageName
            if (pkg in next) continue
            val old = labels[pkg]
            val value = if (canReuse && pkg !in invalid && old != null && old.uid == source.uid && old.path == source.path &&
                old.component == source.component && old.resource == source.resource) old.value else source.loadLabel().trim().ifEmpty { pkg }
            next[pkg] = Label(source.uid, source.path, source.component, source.resource, value)
            entries += AppCatalogEntry(pkg, value, source.uid, pkg == self)
        }
        if (canceled()) return null
        val snapshot = AppCatalogSnapshot(entries = entries.sortedBy { it.label.lowercase() })
        labels = next
        locale = language
        if (changes != null) sequence = changes.sequence
        catalog = snapshot
        return snapshot
    }
}
