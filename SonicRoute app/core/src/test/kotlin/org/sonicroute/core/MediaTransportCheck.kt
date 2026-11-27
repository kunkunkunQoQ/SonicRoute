package org.sonicroute.core

import java.io.File
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit

fun main(args: Array<String>) {
    checkOwnedTrackFocus()
    val work = ArrayDeque<Runnable>()
    val executor = Executor { work.add(it) }
    val credits = mutableListOf<Long>()
    val input = SrapPcmInput(executor, credits::add)
    val packets = File(args[0]).readLines().filter { it.isNotBlank() && !it.startsWith("#") }.map { row ->
        val fields = row.split('|')
        val wire = fields.last().chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        val frame = checkNotNull(SrapCodec.readFrame(wire.inputStream()))
        check(frame.type == SrapFrameType.DATA && frame.streamId == 1L)
        check(frame.payload.size == SrapPcmInput.HEADER + fields[2].toInt() * 4)
        frame.payload
    }
    fun packet(seq: Int, pos: Long) = ByteBuffer.allocate(16).putInt(seq).putLong(pos).putInt(1).array()
    check(!input.receive(packets[1]))
    check(input.receive(packets[0]) && !input.receive(packets[0]) && input.receive(packets[1]))
    check(!input.receive(packet(2, 0)) && !input.receive(ByteArray(3856)) && !input.receive(ByteArray(13)))
    check(input.receive(packet(2, 3)) && input.receive(packet(3, 4)) && !input.receive(packet(4, 5)))
    check(input.take() === packets[0] && credits.isEmpty() && !input.receive(packet(4, 5)))
    input.written()
    check(input.take() === packets[1])
    input.written()
    check(work.size == 1)
    work.removeFirst().run()
    check(credits == listOf(2L) && input.receive(packet(4, 5)))
    repeat(3) { check(input.take() != null); input.written() }
    val waiting = CompletableFuture.supplyAsync { input.take() }
    input.close()
    check(waiting.get(1, TimeUnit.SECONDS) == null && !input.receive(packet(5, 6)))

    val base = TestOperationBridge()
    val uid = UUID.randomUUID().toString()
    val player = MediaPlayerSnapshot(uid, "example.music", "Music", "Track", "", false, 2, 6, 0, 2000, 1.0)
    var activePlayers = listOf(player)
    val initial = base.currentPermissionSnapshot()
    var snapshot = initial.copy(capabilityEntries = initial.capabilityEntries + mapOf(
        Capability.MEDIA_CONTROL to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "active_media", limits = mapOf("packageState" to "1")),
        Capability.CAPTURE_APP to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "other_app:example.music")))
    var completion: ((OperationResult<MediaPlayerSnapshot>) -> Unit)? = null
    var cancelCount = 0
    var captureClosed = 0
    var catalogReads = 0
    var packageReads = 0
    val ops = object : SrapOperationBridge by base {
        override fun currentPermissionSnapshot() = snapshot
        override fun readMedia(sessionId: String, revision: Long): OperationResult<List<MediaPlayerSnapshot>> {
            catalogReads++
            return OperationResult.Done(activePlayers)
        }
        override fun readMediaPackageState(sessionId: String, revision: Long, packageName: String): OperationResult<Boolean> {
            packageReads++
            return when (packageName) {
                "org.sonicroute.app" -> OperationResult.Done(true)
                "bin.mt.plus" -> OperationResult.Done(false)
                "example.multi" -> OperationResult.Failure(ProtocolError.BUSY)
                else -> OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE)
            }
        }
        override fun controlMedia(sessionId: String, revision: Long, key: String, action: String, position: Long?,
            completed: (OperationResult<MediaPlayerSnapshot>) -> Unit): OperationResult<AutoCloseable> {
            completion = completed
            return OperationResult.Done(AutoCloseable { cancelCount++ })
        }
        override fun grantsCapture(session: String, peer: String) = session == "session" && peer == "peer"
        override fun openCapture(session: String, revision: Long, peer: String, app: String) =
            OperationResult.Done(object : PlaybackCapture {
                override fun read(buffer: ByteArray, offset: Int, count: Int): Int { buffer[offset] = 7; return 4 }
                override fun close() { captureClosed++ }
            })
    }
    val media = SrapMediaRequests(ops)
    val replies = mutableListOf<OperationResult<JsonObject>>()
    val watch = StrictJson.obj("watchId" to StrictJson.str(UUID.randomUUID().toString()))
    fun readState(params: JsonObject): OperationResult<JsonObject> {
        media.execute("session", "state", "media.get", snapshot.capabilityRevision, params, replies::add)
        return replies.removeLast()
    }
    fun stateOf(pkg: String) = StrictJson.obj("packageName" to StrictJson.str(pkg), "fields" to StrictJson.str("state"))
    val checkedPackages = mutableSetOf<String>()
    for (row in File(args[1]).readLines(Charsets.UTF_8).filter { it.isNotBlank() && !it.startsWith("#") }) {
        val fields = row.split('|')
        check(fields.size == 3 && checkedPackages.add(fields[0]))
        val params = StrictJson.parse(fields[1]) as JsonObject
        check(params == stateOf(fields[0]))
        val actual = (readState(params) as OperationResult.Done).value
        check(actual == StrictJson.parse(fields[2]) && actual.fields.keys == setOf("packageName", "playing"))
    }
    check(checkedPackages == setOf("org.sonicroute.app", "bin.mt.plus"))
    check((readState(stateOf("example.missing")) as OperationResult.Failure).error == ProtocolError.TARGET_UNAVAILABLE)
    check((readState(stateOf("example.multi")) as OperationResult.Failure).error == ProtocolError.BUSY)
    for (invalid in listOf(stateOf("*"), JsonObject(stateOf("example.music").fields + watch.fields),
        StrictJson.obj("packageName" to StrictJson.str("example.music"), "fields" to StrictJson.str("title")))) {
        check((readState(invalid) as OperationResult.Failure).error == ProtocolError.INVALID_ARGUMENT)
    }
    val withState = snapshot
    snapshot = snapshot.copy(capabilityEntries = snapshot.capabilityEntries +
        (Capability.MEDIA_CONTROL to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "active_media")))
    check((readState(stateOf("example.music")) as OperationResult.Failure).error == ProtocolError.UNSUPPORTED)
    snapshot = withState
    check(packageReads == 4 && catalogReads == 0 && !media.isWatching())
    media.execute("session", "get", "media.get", snapshot.capabilityRevision, watch, replies::add)
    check(replies.removeLast() is OperationResult.Done && media.isWatching())
    for (count in listOf(16, 17)) {
        activePlayers = List(count) { player.copy(sessionKey = UUID.randomUUID().toString()) }
        media.execute("session", "get", "media.get", snapshot.capabilityRevision, StrictJson.obj(), replies::add)
        val limit = (replies.removeLast() as OperationResult.Done).value
        check(limit["truncated"] == JsonBool(count > 16))
        val sent = (limit["players"] as JsonArray).items.map { SrapCodec.requiredText(it as JsonObject, "sessionKey") }
        check(sent == activePlayers.take(16).map { it.sessionKey })
    }
    val command = StrictJson.obj("sessionKey" to StrictJson.str(uid), "action" to StrictJson.str("play"))
    media.execute("session", "change", "media.control", snapshot.capabilityRevision, command, replies::add)
    check(replies.isEmpty())
    media.execute("session", "other", "media.control", snapshot.capabilityRevision, command, replies::add)
    check((replies.removeLast() as OperationResult.Failure).error == ProtocolError.BUSY)
    media.cancel("change")
    completion!!(OperationResult.Done(player))
    check(cancelCount == 1 && (replies.single() as OperationResult.Failure).error == ProtocolError.CANCELED)
    replies.clear()
    media.update(initial)
    check(!media.isWatching())

    val received = mutableListOf<ByteArray>()
    val capture = SrapCapture(ops, executor, { 2L }, { true }, { _, data -> received += data; true }, { _, _ -> })
    val params = StrictJson.obj("appKey" to StrictJson.str("example.music"), "encoding" to StrictJson.str(SrapPcmInput.FORMAT),
        "keepPhoneAudio" to StrictJson.bool(true))
    check(capture.open("wrong", "peer", snapshot.capabilityRevision, params) is OperationResult.Failure)
    check(capture.open("session", "peer", snapshot.capabilityRevision, params) is OperationResult.Done)
    check(received.isEmpty())
    fun credit(n: Int) = capture.credit(StrictJson.obj("streamId" to StrictJson.num(2), "consumedPackets" to StrictJson.num(n)))
    check(credit(1) is OperationResult.Failure && credit(0) is OperationResult.Done)
    while (work.isNotEmpty()) work.removeFirst().run()
    check(received.size == 4 && received.withIndex().all { (i, bytes) ->
        val header = ByteBuffer.wrap(bytes)
        header.int == i && header.long == i.toLong() && bytes[12] == 7.toByte()
    })
    check(credit(5) is OperationResult.Failure && credit(2) is OperationResult.Done)
    work.removeFirst().run()
    check(received.size == 6)
    snapshot = initial
    capture.revalidate()
    check(captureClosed == 1 && credit(2) is OperationResult.Failure)
    media.close()
    capture.close()
    println("PASS owned-track focus sharing/loss/resume, PCM, capture grant/revocation, media commands, 16-player boundary and two package-state vectors")
}
