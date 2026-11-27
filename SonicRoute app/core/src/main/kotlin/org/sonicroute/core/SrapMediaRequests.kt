package org.sonicroute.core

/** Optional foreground/media-session operations for a paired computer. */
interface SrapMediaBridge {
    fun readMedia(sessionId: String, revision: Long): OperationResult<List<MediaPlayerSnapshot>> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)

    fun readMediaPackageState(sessionId: String, revision: Long, packageName: String): OperationResult<Boolean> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)

    fun controlMedia(sessionId: String, revision: Long, key: String, action: String, position: Long?,
        completed: (OperationResult<MediaPlayerSnapshot>) -> Unit): OperationResult<AutoCloseable> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
}

/** One authenticated connection. Explicit watch handles allow two visible PC views to share it. */
internal class SrapMediaRequests(private val bridge: SrapOperationBridge) : AutoCloseable {
    private val lock = Any()
    private val watches = HashSet<String>()
    private var pending: Command? = null
    private var closed = false

    private class Command(val id: String, val lease: OperationLease, val respond: (OperationResult<JsonObject>) -> Unit) {
        var cancel: AutoCloseable? = null
    }

    fun execute(session: String, id: String, op: String, revision: Long, params: JsonObject,
        respond: (OperationResult<JsonObject>) -> Unit) {
        val watchId = if (params.has("watchId")) SrapCodec.requiredText(params, "watchId") else null
        if (watchId != null && !isUuid(watchId)) {
            respond(OperationResult.Failure(ProtocolError.INVALID_ARGUMENT, "invalid_watch_id"))
            return
        }
        if (op == "media.unwatch") {
            if (watchId == null || params.fields.size != 1) respond(OperationResult.Failure(ProtocolError.INVALID_ARGUMENT))
            else {
                synchronized(lock) { watches.remove(watchId) }
                respond(OperationResult.Done(StrictJson.obj()))
            }
            return
        }
        val snapshot = bridge.currentPermissionSnapshot()
        val grant = snapshot.acquireLease(Capability.MEDIA_CONTROL, "active_media", revision)
        if (grant !is OperationResult.Done) {
            respond(grant as OperationResult.Failure)
            return
        }
        if (op == "media.get") {
            if (params.has("packageName") || params.has("fields")) {
                val pkg = (params["packageName"] as? JsonString)?.value
                if (params.fields.size != 2 || pkg == null || !NativeAppRoutingPipe.validPackage(pkg) ||
                    params["fields"] != JsonString("state")) {
                    respond(OperationResult.Failure(ProtocolError.INVALID_ARGUMENT))
                    return
                }
                if (snapshot.entry(Capability.MEDIA_CONTROL).limits["packageState"] != "1") {
                    respond(OperationResult.Failure(ProtocolError.UNSUPPORTED))
                    return
                }
                if (synchronized(lock) { closed }) { respond(OperationResult.Failure(ProtocolError.CANCELED)); return }
                val state = bridge.readMediaPackageState(session, revision, pkg)
                respond(when (state) {
                    is OperationResult.Done -> if (!grant.value.isValidFor(bridge.currentPermissionSnapshot())) {
                        OperationResult.Failure(ProtocolError.PERMISSION_LOST)
                    } else OperationResult.Done(StrictJson.obj("packageName" to StrictJson.str(pkg), "playing" to StrictJson.bool(state.value)))
                    is OperationResult.Failure -> state
                    is OperationResult.Pending -> OperationResult.Failure(ProtocolError.IO_FAILURE)
                })
                return
            }
            if (params.fields.size != (if (watchId == null) 0 else 1)) {
                respond(OperationResult.Failure(ProtocolError.INVALID_ARGUMENT))
                return
            }
            val admitted = synchronized(lock) {
                !closed && (watchId == null || watchId in watches || (watches.size < 8 && watches.add(watchId)))
            }
            if (!admitted) { respond(OperationResult.Failure(ProtocolError.BUSY)); return }
            val result = bridge.readMedia(session, revision)
            if (result !is OperationResult.Done && watchId != null) synchronized(lock) { watches.remove(watchId) }
            respond(when (result) {
                is OperationResult.Done -> if (!grant.value.isValidFor(bridge.currentPermissionSnapshot())) {
                    OperationResult.Failure(ProtocolError.PERMISSION_LOST)
                } else OperationResult.Done(StrictJson.obj(
                    "players" to JsonArray(result.value.take(MAX_SHARED_MEDIA_PLAYERS).map(::encodePlayer)),
                    "truncated" to StrictJson.bool(result.value.size > MAX_SHARED_MEDIA_PLAYERS)))
                is OperationResult.Failure -> result
                is OperationResult.Pending -> OperationResult.Failure(ProtocolError.IO_FAILURE)
            })
            return
        }
        val key = SrapCodec.requiredText(params, "sessionKey")
        val action = SrapCodec.requiredText(params, "action")
        val position = if (params.has("position")) SrapCodec.requiredLong(params, "position") else null
        if (!isUuid(key) || action !in actions || (action == "seek") != (position != null) ||
            (position != null && position !in 0L..604800000L) || params.fields.size != (if (position == null) 2 else 3)) {
            respond(OperationResult.Failure(ProtocolError.INVALID_ARGUMENT, "invalid_media_command"))
            return
        }
        val command = Command(id, grant.value, respond)
        val reserved = synchronized(lock) {
            if (closed || pending != null) false else { pending = command; true }
        }
        if (!reserved) { respond(OperationResult.Failure(ProtocolError.BUSY)); return }
        val handle = try {
            bridge.controlMedia(session, revision, key, action, position) { result ->
                val value = when (result) {
                    is OperationResult.Done -> when {
                        !command.lease.isValidFor(bridge.currentPermissionSnapshot()) ->
                            OperationResult.Failure(ProtocolError.PERMISSION_LOST)
                        result.value.sessionKey != key -> OperationResult.Failure(ProtocolError.STALE_SESSION)
                        else -> OperationResult.Done(StrictJson.obj("player" to encodePlayer(result.value)))
                    }
                    is OperationResult.Failure -> result
                    is OperationResult.Pending -> OperationResult.Failure(ProtocolError.IO_FAILURE)
                }
                complete(command, value)
            }
        } catch (_: Exception) {
            OperationResult.Failure(ProtocolError.IO_FAILURE)
        }
        if (handle is OperationResult.Done) {
            val ended = synchronized(lock) {
                if (pending !== command) true else { command.cancel = handle.value; false }
            }
            if (ended) handle.value.close()
        } else complete(command, handle as? OperationResult.Failure ?: OperationResult.Failure(ProtocolError.IO_FAILURE))
    }

    private fun complete(command: Command, result: OperationResult<JsonObject>) {
        synchronized(lock) {
            if (pending !== command) return
            pending = null
        }
        command.cancel?.close()
        command.respond(result)
    }

    fun isWatching(): Boolean = synchronized(lock) { !closed && watches.isNotEmpty() } &&
        bridge.currentPermissionSnapshot().entry(Capability.MEDIA_CONTROL).status == CapabilityStatus.AVAILABLE

    fun update(snapshot: PermissionSnapshot) {
        val expired = synchronized(lock) {
            if (snapshot.entry(Capability.MEDIA_CONTROL).status != CapabilityStatus.AVAILABLE) watches.clear()
            pending?.takeUnless { it.lease.isValidFor(snapshot) }
        }
        expired?.let { complete(it, OperationResult.Failure(ProtocolError.PERMISSION_LOST)) }
    }

    fun cancel(id: String) {
        val command = synchronized(lock) { pending?.takeIf { it.id == id } } ?: return
        complete(command, OperationResult.Failure(ProtocolError.CANCELED))
    }

    override fun close() {
        val command = synchronized(lock) {
            closed = true
            watches.clear()
            pending.also { pending = null }
        }
        command?.cancel?.close()
    }

    companion object {
        private val actions = setOf("play", "pause", "previous", "next", "seek")
        private val uuid = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
        private fun isUuid(value: String) = value.length == 36 && uuid.matches(value)
        private fun label(value: String): JsonString {
            val text = value.take(128)
            return StrictJson.str(if (text.lastOrNull()?.isHighSurrogate() == true) text.dropLast(1) else text)
        }
        private fun encodePlayer(p: MediaPlayerSnapshot) = StrictJson.obj(
            "sessionKey" to StrictJson.str(p.sessionKey), "package" to StrictJson.str(p.packageName.take(255)),
            "title" to label(p.title), "artist" to label(p.artist), "playing" to StrictJson.bool(p.playing),
            "actions" to StrictJson.num(p.actions), "position" to StrictJson.num(p.positionMs.coerceIn(0,604800000)),
            "duration" to StrictJson.num(p.durationMs.coerceIn(0,604800000)),
            "speed" to JsonNumber(if (p.speed.isFinite()) p.speed.coerceIn(0.0,8.0).toString() else "1"))
    }
}
