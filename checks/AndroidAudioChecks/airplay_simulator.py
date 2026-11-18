"""Independent loopback sender for the SonicRoute AirPlay receiver. See AIRPLAY-CHECKS.md."""

import argparse
import base64
import hashlib
import json
import math
import os
import plistlib
import re
import socket
import struct
import subprocess
import time
from pathlib import Path
from fractions import Fraction

import av
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.asymmetric import ed25519, x25519
from cryptography.hazmat.primitives.hashes import SHA512
from cryptography.hazmat.primitives.kdf.hkdf import HKDF


# RFC 5054 appendix A, 3072-bit group.
N = int("""
FFFFFFFF FFFFFFFF C90FDAA2 2168C234 C4C6628B 80DC1CD1
29024E08 8A67CC74 020BBEA6 3B139B22 514A0879 8E3404DD
EF9519B3 CD3A431B 302B0A6D F25F1437 4FE1356D 6D51C245
E485B576 625E7EC6 F44C42E9 A637ED6B 0BFF5CB6 F406B7ED
EE386BFB 5A899FA5 AE9F2411 7C4B1FE6 49286651 ECE45B3D
C2007CB8 A163BF05 98DA4836 1C55D39A 69163FA8 FD24CF5F
83655D23 DCA3AD96 1C62F356 208552BB 9ED52907 7096966D
670C354E 4ABC9804 F1746C08 CA18217C 32905E46 2E36CE3B
E39E772C 180E8603 9B2783A2 EC07A28F B5C55DF0 6F4C52C9
DE2BCBF6 95581718 3995497C EA956AE5 15D22618 98FA0510
15728E5A 8AAAC42D AD33170D 04507A33 A85521AB DF1CBA64
ECFB8504 58DBEF0A 8AEA7157 5D060C7D B3970F85 A6E1E4C7
ABF5AE8C DB0933D7 1E8C94E0 4A25619D CEE3D226 1AD2EE6B
F12FFA06 D98A0864 D8760273 3EC86A64 521F2B18 177B200C
BBE11757 7A615D6C 770988C0 BAD946E2 08E24FA0 74E5AB31
43DB5BFC E0FD108E 4B82D120 A93AD2CA FFFFFFFF FFFFFFFF
""".replace(" ", "").replace("\n", ""), 16)
G = 5
PLIST = "application/x-apple-binary-plist"
OCTETS = "application/octet-stream"


def h(*parts):
    return hashlib.sha512(b"".join(parts)).digest()


def bn(value, width=None):
    return value.to_bytes(width or (value.bit_length() + 7) // 8, "big")


def tlv(*fields):
    return b"".join(bytes([key, len(chunk)]) + chunk for key, value in fields
                    for chunk in (value[i:i + 255] for i in range(0, len(value), 255)))


def unpack_tlv(raw):
    result = {}
    while raw:
        key, size = raw[:2]
        result[key] = result.get(key, b"") + raw[2:2 + size]
        raw = raw[2 + size:]
    return result


class Sender:
    def __init__(self, port):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=5)
        self.socket.settimeout(5)
        self.wire = self.socket.makefile("rb")
        self.sequence = self.output_nonce = self.input_nonce = 0
        self.decryptor = self.encryptor = None
        self.target = f"rtsp://127.0.0.1:{port}/123456"
        self.event = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        if self.event:
            self.event.close()
        self.wire.close()
        self.socket.close()

    def request(self, method, target=None, body=b"", kind=None, extra="", expected=200):
        self.sequence += 1
        path = target or self.target
        if kind:
            extra += f"Content-Type: {kind}\r\n"
        header = (f"{method} {path} RTSP/1.0\r\nCSeq: {self.sequence}\r\n"
                  f"User-Agent: AirPlaySim/1.0\r\n{extra}Content-Length: {len(body)}\r\n\r\n").encode("ascii")
        if self.encryptor:
            plain = header + body
            for i in range(0, len(plain), 1024):
                chunk = plain[i:i + 1024]
                prefix = struct.pack("<H", len(chunk))
                self.socket.sendall(prefix + self.encryptor.encrypt(struct.pack("<IQ", 0, self.output_nonce), chunk, prefix))
                self.output_nonce += 1
            reply = self.read_encrypted()
        else:
            self.socket.sendall(header + body)
            lines = bytearray()
            while True:
                line = self.wire.readline()
                assert line, f"EOF before {method} response"
                lines += line
                if line == b"\r\n":
                    break
            reply = bytes(lines)
        headers, content = reply.split(b"\r\n\r\n", 1)
        status = int(headers.split(b" ")[1])
        assert status == expected, f"{method} {path}: expected {expected}, got {status}"
        assert f"CSeq: {self.sequence}\r\n".encode() in headers + b"\r\n"
        length = self.length(headers)
        if not self.decryptor:
            content = self.wire.read(length)
        assert len(content) == length
        return content

    @staticmethod
    def length(headers):
        return next(int(line.split(b":", 1)[1]) for line in headers.split(b"\r\n")
                    if line.lower().startswith(b"content-length:"))

    def read_encrypted(self):
        reply = b""
        while True:
            prefix = self.wire.read(2)
            assert len(prefix) == 2, "EOF in secured RTSP"
            size = int.from_bytes(prefix, "little")
            reply += self.decryptor.decrypt(struct.pack("<IQ", 0, self.input_nonce), self.wire.read(size + 16), prefix)
            self.input_nonce += 1
            if b"\r\n\r\n" in reply:
                header, content = reply.split(b"\r\n\r\n", 1)
                if len(content) >= self.length(header):
                    return reply

    def pair(self, pin=b"3939"):
        info = plistlib.loads(self.request("GET", "/info"))
        assert info["name"] == "SonicRoute Simulation" and info["features"] & (1 << 48)
        challenge = unpack_tlv(self.request("POST", "/pair-setup",
                                           tlv((6, b"\x01"), (0, b"\x00"), (19, b"\x10\0\0\0")),
                                           "application/pairing+tlv8", "X-Apple-HKP: 4\r\n"))
        assert challenge[6] == b"\x02"
        salt, public_b = challenge[2], int.from_bytes(challenge[3], "big")
        secret_a = int.from_bytes(os.urandom(32), "big")
        public_a = pow(G, secret_a, N)
        k = int.from_bytes(h(bn(N, 384), bn(G, 384)), "big")
        u = int.from_bytes(h(bn(public_a, 384), bn(public_b, 384)), "big")
        x = int.from_bytes(h(salt, h(b"Pair-Setup:" + pin)), "big")
        shared = pow((public_b - k * pow(G, x, N)) % N, secret_a + u * x, N)
        key = h(bn(shared))
        group_proof = bytes(a ^ b for a, b in zip(h(bn(N)), h(bn(G))))
        proof = h(group_proof, h(b"Pair-Setup"), salt, bn(public_a), bn(public_b), key)
        result = unpack_tlv(self.request("POST", "/pair-setup", tlv((6, b"\x03"), (3, bn(public_a)), (4, proof)),
                                         "application/pairing+tlv8", "X-Apple-HKP: 4\r\n"))
        if pin != b"3939":
            assert result == {6: b"\x04", 7: b"\x02"}
            return None
        assert result[6] == b"\x04" and result[4] == h(bn(public_a), proof, key), "server SRP proof"
        for direction in ("Read", "Write"):
            info = f"Control-{direction}-Encryption-Key".encode()
            cipher = ChaCha20Poly1305(HKDF(algorithm=SHA512(), length=32, salt=b"Control-Salt", info=info).derive(key))
            setattr(self, "decryptor" if direction == "Read" else "encryptor", cipher)
        return key[:32]

    def setup(self, protocol="NTP", expected=200):
        answer = self.plist("SETUP", {"timingProtocol": protocol, "name": "模拟 iPhone", "deviceID": "02:00:00:01:02:03",
                                      "sessionUUID": "00000000-0000-0000-0000-000000000001"}, expected=expected)
        if expected == 200:
            self.event = socket.create_connection(("127.0.0.1", answer["eventPort"]), timeout=5)
        return answer

    def plist(self, method, payload, expected=200):
        raw = self.request(method, body=plistlib.dumps(payload, fmt=plistlib.FMT_BINARY), kind=PLIST, expected=expected)
        return plistlib.loads(raw) if raw else None

    def open_media(self, stream_type, codec, format_id, samples, key):
        answer = self.plist("SETUP", {"streams": [{"type": stream_type, "ct": codec, "audioFormat": 1 << format_id,
                                                 "spf": samples, "shk": key, "controlPort": 0}]})["streams"][0]
        assert answer["type"] == stream_type and answer["dataPort"] and answer["controlPort"]
        return answer

    def verify_mirror_pair(self, valid=True):
        info = plistlib.loads(self.request("GET", "/info"))
        assert info["features"] & (1 << 7) and info["displays"][0]["width"] > 0
        signing = ed25519.Ed25519PrivateKey.generate()
        public = signing.public_key().public_bytes_raw()
        receiver_key = self.request("POST", "/pair-setup", public, OCTETS)
        assert receiver_key == info["pk"]
        ephemeral = x25519.X25519PrivateKey.generate()
        coordinate = ephemeral.public_key().public_bytes_raw()
        reply = self.request("POST", "/pair-verify", b"\x01\0\0\0" + coordinate + public, OCTETS)
        assert len(reply) == 96
        server = reply[:32]
        shared = ephemeral.exchange(x25519.X25519PublicKey.from_public_bytes(server))
        cipher = Cipher(algorithms.AES(h(b"Pair-Verify-AES-Key", shared)[:16]),
                        modes.CTR(h(b"Pair-Verify-AES-IV", shared)[:16])).encryptor()
        ed25519.Ed25519PublicKey.from_public_bytes(receiver_key).verify(cipher.update(reply[32:]), server + coordinate)
        signature = signing.sign(coordinate + server) if valid else bytes(64)
        self.request("POST", "/pair-verify", b"\0" * 4 + cipher.update(signature), OCTETS, expected=200 if valid else 403)
        return shared

    def start_mirror(self, shared, timing, stream_id, unsupported_audio=False):
        # Independent PlayFair known-answer vector from donvito/iphone-mirror, 2721cda, fp_test.go.
        self.request("POST", "/fp-setup", b"malformed", OCTETS, expected=400)
        hello = bytes.fromhex("46504c590301010000000004020002bb")
        assert len(self.request("POST", "/fp-setup", hello, OCTETS)) == 142
        record = bytearray((i * 7) % 256 for i in range(164))
        record[:13] = bytes.fromhex("46504c59030103000000009802")
        proof = self.request("POST", "/fp-setup", record, OCTETS)
        assert len(proof) == 32 and proof[12:] == record[144:]
        wrapped = bytes((i * 13) % 256 for i in range(72))
        raw_key = bytes.fromhex("8474d42ea2f1423c092d8759b6c51903")
        key = h(raw_key, shared)[:16]
        iv = b"\x13" * 16
        connection = self.plist("SETUP", {"timingProtocol": "NTP", "timingPort": timing.getsockname()[1],
                                          "name": "模拟投屏", "ekey": wrapped, "eiv": iv})
        probe, peer = timing.recvfrom(32)
        assert len(probe) == 32 and probe[:2] == b"\x80\xd2" and peer[1] == connection["timingPort"]
        self.event = socket.create_connection(("127.0.0.1", connection["eventPort"]), timeout=5)
        streams = self.plist("SETUP", {"streams": [{"type": 110, "streamConnectionID": stream_id},
                                                   {"type": 96, "ct": 8 if unsupported_audio else 1, "usingScreen": True}]})["streams"]
        video, audio = (next(s for s in streams if s["type"] == kind) for kind in (110, 96))
        self.request("RECORD")
        suffix = str(stream_id).encode("ascii")
        cipher = Cipher(algorithms.AES(h(b"AirPlayStreamKey" + suffix, key)[:16]),
                        modes.CTR(h(b"AirPlayStreamIV" + suffix, key)[:16])).encryptor()
        return video["dataPort"], audio["dataPort"], cipher, key, iv


def video_fixture(width, height, hevc=False):
    encoder = av.CodecContext.create("libx265" if hevc else "libx264", "w")
    encoder.width, encoder.height = width, height
    encoder.pix_fmt, encoder.time_base = "yuv420p", Fraction(1, 30)
    encoder.options = {"preset": "ultrafast", "tune": "zerolatency"}
    if hevc:
        encoder.options["x265-params"] = "log-level=error:pools=1:frame-threads=1:bframes=0"
    payloads = []
    config = None
    for index in range(5):
        frame = av.VideoFrame(width, height, "yuv420p")
        frame.pts = index
        for plane, level in zip(frame.planes, (64 + index * 10, 80, 171)):
            plane.update(bytes([level]) * plane.buffer_size)
        for packet in encoder.encode(frame):
            nals = [nal for nal in re.split(b"\0\0\0?\x01", bytes(packet)) if nal]
            def nal_type(nal):
                return (nal[0] >> 1) & 63 if hevc else nal[0] & 31

            parameter_types = (32, 33, 34) if hevc else (7, 8)
            if config is None:
                parameter_sets = [next(nal for nal in nals if nal_type(nal) == kind) for kind in parameter_types]
                if hevc:
                    # Visual sample entry enclosing a standard HEVC decoder configuration box.
                    record = b"\x01" + bytes(20) + b"\xff\x03"
                    record += b"".join(struct.pack("!BHH", 0x80 | kind, 1, len(nal)) + nal
                                       for kind, nal in zip(parameter_types, parameter_sets))
                    config = struct.pack("!I4s", 94 + len(record), b"hvc1") + bytes(78)
                    config += struct.pack("!I4s", 8 + len(record), b"hvcC") + record
                else:
                    sps, pps = parameter_sets
                    config = b"\x01" + sps[1:4] + b"\xff\xe1" + struct.pack("!H", len(sps)) + sps + b"\x01" + struct.pack("!H", len(pps)) + pps
            payloads.append(b"".join(struct.pack("!I", len(nal)) + nal for nal in nals if nal_type(nal) not in parameter_types))
    assert config and len(payloads) == 5 and all(payloads)
    return config, payloads


def screen_packet(kind, payload, timestamp, width=0, height=0):
    header = bytearray(128)
    struct.pack_into("<IB", header, 0, len(payload), kind)
    struct.pack_into("<Q", header, 8, timestamp)
    struct.pack_into("<ff", header, 40, width, height)
    return header + payload


def encrypted_packet(payload, key, count, ts, kind):
    nonce = struct.pack("<IQ", 0, count)
    header = struct.pack("!BBHII", 0x80, kind, count & 65535, ts, 0x12345678)
    return header + ChaCha20Poly1305(key).encrypt(nonce, payload, header[4:]) + nonce[4:]


def tone_packets(codec, sample_rate, frames):
    encoder = av.CodecContext.create(codec, "w")
    encoder.sample_rate = sample_rate
    encoder.layout = "stereo"
    encoder.format = "s16p" if codec == "alac" else "fltp"
    outputs = []
    for block in range(3):
        frame = av.AudioFrame(format=encoder.format.name, layout="stereo", samples=frames)
        frame.sample_rate = sample_rate
        frame.pts = block * frames
        values = [0.42 * math.sin(2 * math.pi * 437 * (i + frame.pts) / sample_rate) for i in range(frames)]
        if codec == "alac":
            audio = struct.pack(f"<{frames}h", *(int(v * 32767) for v in values))
        else:
            audio = struct.pack(f"<{frames}f", *values)
        for plane in frame.planes:
            plane.update(audio.ljust(plane.buffer_size, b"\0"))
        outputs.extend(bytes(packet) for packet in encoder.encode(frame))
    outputs.extend(bytes(packet) for packet in encoder.encode(None))
    assert outputs and all(outputs)
    return outputs


def verify_discovery(raw, port):
    assert struct.unpack_from("!6H", raw) == (0, 0x8400, 0, 9, 0, 0)
    offset = 12

    def name(data, index):
        labels = []
        while data[index]:
            size = data[index]
            labels.append(data[index + 1:index + 1 + size].decode())
            index += 1 + size
        return ".".join(labels), index + 1

    records = []
    for _ in range(9):
        owner, offset = name(raw, offset)
        kind, _, ttl, length = struct.unpack_from("!HHIH", raw, offset)
        offset += 10
        payload = raw[offset:offset + length]
        records.append((owner, kind, payload))
        assert ttl == 120 and len(payload) == length
        offset += length
    assert offset == len(raw) and len({owner for owner, _, _ in records}) >= 5
    for suffix in ("_airplay._tcp.local", "_raop._tcp.local"):
        instance = next(name(data, 0)[0] for owner, kind, data in records if owner == suffix and kind == 12)
        srv = next(data for owner, kind, data in records if owner == instance and kind == 33)
        assert int.from_bytes(srv[4:6], "big") == port and any(owner == instance and kind == 16 for owner, kind, _ in records)
        host, _ = name(srv, 6)
        assert (host, 1, socket.inet_aton("127.0.0.1")) in records


def run(dotnet, assembly, output, require_hevc=False):
    results = []

    def mark(label):
        results.append(label)
        print("PASS " + label, flush=True)

    args = [dotnet, assembly, "--airplay-listen"] + (["--prefer-hevc"] if require_hevc else [])
    with subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          text=True, encoding="utf-8", env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en"}) as proc:
        try:
            ready = json.loads(proc.stdout.readline())
            port = ready["Port"]

            def command(value="status"):
                proc.stdin.write(value + "\n")
                proc.stdin.flush()
                return json.loads(proc.stdout.readline())

            def wait_for(predicate):
                limit = time.monotonic() + 4
                while True:
                    state = command()
                    if predicate(state):
                        return state
                    assert time.monotonic() < limit, state
                    time.sleep(0.02)

            verify_discovery(base64.b64decode(ready["Mdns"]), port)
            mark("DNS-SD AirPlay and RAOP records resolve to the listening port")
            with Sender(port) as sender:
                sender.plist("SETUP", {"timingProtocol": "NTP"}, expected=403)
            with Sender(port) as sender:
                sender.pair(pin=b"0101")
                assert command()["Outputs"] == 0
            mark("Plaintext streaming and invalid SRP proof cannot open audio")

            with Sender(port) as sender:
                key = sender.pair()
                timing = sender.setup()
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as ntp:
                    ntp.settimeout(3)
                    sent = b"\x80\xd2" + b"\0" * 22 + b"\x01\x23\x45\0\x06\x78\x90\0"
                    ntp.sendto(sent, ("127.0.0.1", timing["timingPort"]))
                    reply, _ = ntp.recvfrom(64)
                    assert len(reply) == 32 and reply[:2] == b"\x80\xd3" and reply[8:16] == sent[24:]
                mark("Independent SRP proof, encrypted control and NTP timing exchange")
                with Sender(port) as second:
                    second.pair()
                    second.setup(expected=453)
                sender.request("RECORD")
                sender.request("OPTIONS", extra="X-Long-Header: " + "x" * 1500 + "\r\n")
                mark("One receiver source and fragmented encrypted RTSP")
                sender.plist("SETUP", {"streams": [{"type": 110, "streamConnectionID": 123}]}, expected=400)
                stream = sender.open_media(96, 2, 18, 4096, key)
                frames = tone_packets("alac", 44100, 4096)
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as media:
                    data = encrypted_packet(frames[0], key, 1, 3000, 96)
                    corrupt = bytearray(data)
                    corrupt[-14] ^= 1
                    media.sendto(corrupt, ("127.0.0.1", stream["dataPort"]))
                    time.sleep(0.08)
                    assert command()["Outputs"] == 0
                    for index, frame in enumerate(frames):
                        data = encrypted_packet(frame, key, index + 1, 3000 + index * 4096, 96)
                        media.sendto(data, ("127.0.0.1", stream["dataPort"]))
                        time.sleep(0.075)
                    state = wait_for(lambda s: (s["NonSilent"] or 0) > 1000)
                    before = state["SourceSamples"]
                    media.sendto(data, ("127.0.0.1", stream["dataPort"]))
                    time.sleep(0.06)
                    assert command()["SourceSamples"] == before and state["Sender"] == "模拟 iPhone"
                mark("Authenticated ALAC over UDP decodes to non-silent PCM and rejects corrupt and repeated packets")
                chosen = command("controls")
                assert chosen["Volume"] == 37 and chosen["Muted"] and chosen["Endpoint"] == "simulated-endpoint" and chosen["Disposed"] == 1
                chosen = command("system")
                assert chosen["Endpoint"] is None
                command("default")
                wait_for(lambda s: s["Outputs"] == 4 and s["Disposed"] == 3)
                mark("Volume, mute, explicit route and default-route notification reuse the live source")
                sender.request("TEARDOWN")
                wait_for(lambda s: s["Stage"] == "Waiting" and s["Disposed"] == s["Outputs"])
                mark("Sender teardown disposes original output and releases the receiver")

            with Sender(port) as sender:
                key = sender.pair()
                ptp = sender.setup(protocol="PTP")
                assert ptp["timingPort"] == 0 and ptp["timingPeerInfo"]["Addresses"] == ["127.0.0.1"]
                # A one-step sender clock over the same Windows loopback interface.
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as timing_socket:
                    timing_socket.sendto(b"\0\x02\0\x2c" + b"\0" * 30 + (42).to_bytes(6, "big") + b"\0" * 4,
                                         ("127.0.0.1", 319))
                sender.request("RECORD")
                stream = sender.open_media(103, 4, 23, 1024, key)
                frames = tone_packets("aac", 48000, 1024)
                with socket.create_connection(("127.0.0.1", stream["dataPort"]), timeout=5) as media:
                    for index, frame in enumerate(frames):
                        packet = encrypted_packet(frame, key, index + 100, 9000 + index * 1024, 103)
                        media.sendall(struct.pack("!H", len(packet) + 2) + packet)
                    assert command()["Stage"] == "Waiting"
                    sender.plist("SETRATEANCHORTIME", {"rate": 1.0, "rtpTime": 9000, "networkTimeSecs": 42,
                                                      "networkTimeFrac": 0x2000000000000000})
                    wait_for(lambda s: (s["NonSilent"] or 0) > 1000)
                    sender.plist("SETRATEANCHORTIME", {"rate": 0, "rtpTime": 9000})
                    assert command()["Pending"] == 0
                    mark("Buffered AAC over TCP waits for play, renders PCM and clears on pause")
                    command("disconnect")
                    wait_for(lambda s: s["Stage"] == "Waiting" and s["Disposed"] == s["Outputs"])
                    try:
                        assert media.recv(1) == b""
                    except ConnectionResetError:
                        pass  # Windows cancels an in-flight read before closing the socket.
                mark("Computer disconnect closes the audio socket and retires output")

            with Sender(port) as sender:
                sender.verify_mirror_pair(valid=False)
                assert not command()["Mirroring"]
            mark("Invalid legacy pair verification does not open a mirror session")

            with Sender(port) as sender, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as timing:
                timing.bind(("127.0.0.1", 0))
                timing.settimeout(5)
                shared = sender.verify_mirror_pair()
                video_port, audio_port, cipher, key, iv = sender.start_mirror(shared, timing, 0xE1234567890ABCDE)
                with socket.create_connection(("127.0.0.1", video_port), timeout=5) as video, \
                     socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as audio:
                    assert command()["MirrorFrames"] == 0
                    config, frames = video_fixture(320, 180)
                    video.sendall(screen_packet(1, config, 1 << 32, 320, 180))
                    for i, frame in enumerate(frames):
                        packet = screen_packet(0, cipher.update(frame), (1 << 32) + i * (1 << 32) // 30)
                        video.sendall(packet[:37])
                        video.sendall(packet[37:])
                    state = wait_for(lambda s: s["Mirroring"] and s["MirrorFrames"] >= 3)
                    assert state["Sender"] == "模拟投屏"
                    mark("Verified legacy key exchange and fragmented TCP mirror video produce Windows-rendered H.264 frames")

                    for i in range(4):
                        pcm = struct.pack("<704h", *(5000 if j % 16 >= 8 else -5000 for j in range(704)))
                        cbc = Cipher(algorithms.AES(key), modes.CBC(iv)).encryptor()
                        header = struct.pack("!BBHII", 0x80, 96, i, i * 352, 0x12345678)
                        audio.sendto(header + cbc.update(pcm) + cbc.finalize(), ("127.0.0.1", audio_port))
                        time.sleep(0.012)
                    wait_for(lambda s: s["Mirroring"] and (s["NonSilent"] or 0) > 1000)
                    mark("Mirroring and supported legacy PCM audio run together")
                    sender.request("TEARDOWN")
                    wait_for(lambda s: s["Stage"] == "Waiting" and not s["Mirroring"] and s["Disposed"] == s["Outputs"])
                    try:
                        assert video.recv(1) == b""
                    except ConnectionResetError:
                        pass
                mark("Mirror teardown closes video and audio and returns to waiting")

            if require_hevc:
                with Sender(port) as sender, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as timing:
                    info = plistlib.loads(sender.request("GET", "/info"))
                    assert info["features"] & (1 << 42), "Windows HEVC decoder could not be opened"
                    timing.bind(("127.0.0.1", 0))
                    timing.settimeout(5)
                    shared = sender.verify_mirror_pair()
                    video_port, _, cipher, _, _ = sender.start_mirror(shared, timing, 0x1020304050607080, unsupported_audio=True)
                    with socket.create_connection(("127.0.0.1", video_port), timeout=5) as video:
                        config, frames = video_fixture(192, 320, hevc=True)
                        video.sendall(screen_packet(1, config, 1 << 32, 192, 320))
                        for i, frame in enumerate(frames):
                            video.sendall(screen_packet(0, cipher.update(frame), (1 << 32) + i * (1 << 32) // 30))
                        state = wait_for(lambda s: s["Mirroring"] and s["MirrorFrames"] >= 3)
                        assert state["MirrorAudioUnavailable"] and state["Id"] is None
                        sender.request("TEARDOWN")
                        wait_for(lambda s: s["Stage"] == "Waiting" and not s["Mirroring"])
                mark("Optional HEVC discovery and hvcC configuration produce rendered H.265 frames in a new session")
                mark("Unsupported AAC-ELD mirror sound leaves video active and opens no audio output")

            proc.stdin.write("stop\n")
            proc.stdin.flush()
            assert json.loads(proc.stdout.readline())["Closed"]
            assert proc.wait(timeout=8) == 0
            mark("Receiver stop releases the control listener")
        finally:
            if proc.poll() is None:
                proc.kill()
    output.write_text(json.dumps({"passed": results, "real_apple_device": False, "real_audio_output": False},
                                 ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    args = argparse.ArgumentParser()
    args.add_argument("dotnet")
    args.add_argument("assembly")
    args.add_argument("output", type=Path)
    args.add_argument("--require-hevc", action="store_true")
    run(**vars(args.parse_args()))
