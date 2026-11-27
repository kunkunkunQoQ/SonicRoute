package org.sonicroute.core

import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

fun main(args: Array<String>) {
    val vector = readBusinessVectors(args.single()).getValue("display.annexb")
    val base = TestOperationBridge()
    val initial = base.currentPermissionSnapshot()
    val snapshot = initial.copy(capabilityEntries = initial.capabilityEntries +
        (Capability.MIRROR_DISPLAY to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "one_projection")))
    val stopped = AtomicInteger()
    var grant = true
    lateinit var frames: ScreenFrames
    val ops = object : SrapOperationBridge by base {
        override fun currentPermissionSnapshot() = snapshot
        override fun grantsDisplay(session: String, peer: String) = grant && session == "chosen" && peer == "pin"
        override fun openDisplay(session: String, peer: String, revision: Long): OperationResult<ScreenFrames> {
            frames = ScreenFrames(720, 1280) { stopped.incrementAndGet() }
            return OperationResult.Done(frames)
        }
    }
    val chunks = CopyOnWriteArrayList<ByteArray>()
    var id = 0L
    val manager = SrapDisplay(ops, { true }, { id += 2; id }, { _, bytes -> chunks += bytes; true })
    val codec = StrictJson.obj("codec" to StrictJson.str("h264"))
    fun open(session: String = "chosen") = manager.open(session, "pin", snapshot.capabilityRevision, codec, "start")
    fun next(seq: Long, request: String = "next"): CompletableFuture<OperationResult<JsonObject>> {
        val result = CompletableFuture<OperationResult<JsonObject>>()
        manager.next(request, StrictJson.obj("streamId" to StrictJson.num(id), "seq" to StrictJson.num(seq)), result::complete)
        return result
    }
    fun ok(f: CompletableFuture<OperationResult<JsonObject>>) = (f.get(3, TimeUnit.SECONDS) as OperationResult.Done).value
    val config = ScreenUnit(vector.frame.payload, SrapCodec.requiredLong(vector.metadata, "ptsUs"),
        (vector.metadata["config"] as JsonBool).value, (vector.metadata["key"] as JsonBool).value)
    val key = ScreenUnit(ByteArray(40_000) { 8 }, 700, false, true)
    val delta = ScreenUnit(byteArrayOf(2,3), 900, false, false)
    try {
        check(open("another") is OperationResult.Failure)
        check(SrapCodec.requiredLong((open() as OperationResult.Done).value, "streamId") == 2L)
        check(open() is OperationResult.Failure && chunks.isEmpty())
        val pending = next(SrapCodec.requiredLong(vector.metadata, "seq"))
        check(!pending.isDone && (next(0, "other").get() as OperationResult.Failure).error == ProtocolError.BUSY)
        frames.offer(config); frames.offer(delta)
        check(!pending.isDone && chunks.isEmpty())
        frames.offer(key)
        check(ok(pending).fields == vector.metadata.fields && chunks.single().contentEquals(vector.frame.payload))
        check(ok(next(1))["key"] == JsonBool(true) && chunks.map { it.size } == listOf(config.bytes.size,32768,7232))
        check(next(1).get() is OperationResult.Failure)
        repeat(4) { check(!frames.offer(delta)) }
        check(frames.offer(delta))
        val recovering = next(2)
        check(!recovering.isDone)
        frames.offer(key)
        check(ok(recovering)["config"] == JsonBool(true) && ok(next(3))["key"] == JsonBool(true))
        val revoked = next(4)
        grant = false
        manager.revalidate()
        check(revoked.get(3, TimeUnit.SECONDS) is OperationResult.Failure && stopped.get() == 1)
        grant = true
        check(open() is OperationResult.Done)
        manager.cancelRequest("start")
        check(stopped.get() == 2)
        check(open() is OperationResult.Done)
        val canceled = next(0)
        manager.cancelRequest("next")
        check(canceled.get(3, TimeUnit.SECONDS) is OperationResult.Failure && stopped.get() == 3)
        println("Screen protocol fixture passed: session grant, even DATA, frame-driven waits, bounded chunks, keyframe recovery, cancellation")
    } finally { manager.close() }
}
