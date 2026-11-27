package org.sonicroute.core

import java.io.Reader

/** One platform track from a known playback-thread table. Other UIDs and unrelated sessions are dropped. */
data class NativeFlingerTrack(val streamType: Int, val usage: Int, val physicalOutputs: Set<String>) {
    companion object {
        private val whitespace = Regex("\\s+")
        private val devices = Regex("AUDIO_DEVICE_OUT_[A-Z0-9_]+")
        private val row = Regex("^\\s*(?:[^\\s]+\\s+)?[0-9]+\\s+(yes|no)\\s+[0-9]+\\s*/\\s*([0-9]+)\\s+([0-9]+)\\s+(.*)$")
        private const val header = "Type Id Active Client(pid/uid) Session Port Id S Flags Format Chn mask SRate ST Usg CT"
        private const val lineLimit = 65536
        private const val dumpLimit = 4 * 1024 * 1024
        private class DumpLimit : Exception()

        fun confirmOutputs(framework: Set<Int>, mixer: Set<Int>, requireMixer: Boolean): Set<Int> = when {
            mixer.isEmpty() -> if (requireMixer) emptySet() else framework
            framework.isEmpty() || framework == mixer -> mixer
            else -> emptySet()
        }

        fun read(reader: Reader, uid: Int, sessions: Set<Int>): Map<Int, List<NativeFlingerTrack>> = try {
            val lines = sequence {
                val buffer = CharArray(8192)
                val line = StringBuilder()
                var total = 0
                while (true) {
                    val count = reader.read(buffer)
                    if (count < 0) break
                    total += count
                    if (total > dumpLimit) throw DumpLimit()
                    for (index in 0 until count) {
                        if (buffer[index] == '\n') { yield(line.toString()); line.setLength(0) }
                        else {
                            if (line.length >= lineLimit) throw DumpLimit()
                            line.append(buffer[index])
                        }
                    }
                }
                if (line.isNotEmpty()) yield(line.toString())
            }
            read(lines, uid, sessions)
        } catch (_: DumpLimit) { emptyMap() }

        fun read(lines: Sequence<String>, uid: Int, sessions: Set<Int>): Map<Int, List<NativeFlingerTrack>> {
            val found = mutableMapOf<Int, MutableList<NativeFlingerTrack>>()
            var output = false
            var running = false
            var table = false
            var ports = emptySet<String>()
            var chars = 0
            for (line in lines) {
                chars += line.length + 1
                if (chars > dumpLimit || line.length > lineLimit) return emptyMap()
                val trimmed = line.trim()
                when {
                    trimmed.startsWith("Output thread ") || trimmed.startsWith("Input thread ") -> {
                        output = trimmed.startsWith("Output thread "); running = false; table = false; ports = emptySet()
                    }
                    !output -> Unit
                    trimmed.startsWith("Standby:") -> running = trimmed.substringAfter(':').trim() == "no"
                    trimmed.startsWith("Output devices:") -> ports = devices.findAll(trimmed).map { it.value }.toSet()
                    trimmed.startsWith("Type ") -> table = trimmed.replace(whitespace, " ").startsWith(header)
                    table && running && ports.isNotEmpty() -> {
                        val cols = row.matchEntire(line)
                        if (cols == null) { table = false; continue }
                        if (cols.groupValues[1] != "yes" || cols.groupValues[2].toIntOrNull() != uid) continue
                        val session = cols.groupValues[3].toIntOrNull() ?: continue
                        if (session !in sessions) continue
                        val fields = cols.groupValues[4].split(whitespace, 10)
                        val streamType = fields.getOrNull(6)?.toIntOrNull() ?: return emptyMap()
                        val usage = fields.getOrNull(7)?.toIntOrNull() ?: return emptyMap()
                        found.getOrPut(session) { mutableListOf() }.add(NativeFlingerTrack(streamType, usage, ports))
                    }
                }
            }
            return found
        }
    }
}
