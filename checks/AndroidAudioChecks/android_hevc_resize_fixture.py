"""Write a synthetic HEVC virtual-display stream for --android-hevc-recovery."""

from fractions import Fraction
from pathlib import Path
import re
import struct
import sys

import av


CONFIG = 1 << 63
KEY_FRAME = 1 << 62


def make_sequence(width, height, shade):
    encoder = av.CodecContext.create("libx265", "w")
    encoder.width = width
    encoder.height = height
    encoder.pix_fmt = "yuv420p"
    encoder.time_base = Fraction(1, 30)
    encoder.options = {
        "preset": "ultrafast",
        "tune": "zerolatency",
        "x265-params": "log-level=error:pools=1:frame-threads=1:bframes=0:repeat-headers=1",
    }
    pictures = []
    for index in range(8):
        frame = av.VideoFrame(width, height, "yuv420p")
        frame.pts = index
        for plane, value in zip(frame.planes, (shade + index * 2, 128, 128)):
            plane.update(bytes([value]) * plane.buffer_size)
        pictures.extend(bytes(packet) for packet in encoder.encode(frame))
    pictures.extend(bytes(packet) for packet in encoder.encode())

    first = pictures[0]
    offsets = [match for match in re.finditer(rb"\x00\x00\x00?\x01", first)]
    config = bytearray()
    keyframe = bytearray()
    for index, start in enumerate(offsets):
        end = offsets[index + 1].start() if index + 1 < len(offsets) else len(first)
        nal_type = (first[start.end()] >> 1) & 0x3F
        target = config if nal_type in (32, 33, 34) else keyframe
        target.extend(first[start.start():end])
    assert config and keyframe and len(pictures) == 8
    pictures[0] = bytes(keyframe)
    return config, pictures


def write_fixture(output):
    with output.open("wb") as stream:
        stream.write(b"Simulated Android".ljust(64, b"\0"))
        stream.write(struct.pack("!4sII", b"h265", 640, 480))

        def write_packet(payload, flags):
            stream.write(struct.pack("!QI", flags, len(payload)))
            stream.write(payload)

        previous_delta = None
        pts = 0
        for index, (width, height, shade) in enumerate(((640, 480, 64), (1280, 992, 96), (848, 640, 128))):
            config, pictures = make_sequence(width, height, shade)
            write_packet(config, CONFIG)
            if previous_delta is not None:
                # The new sequence must wait for a picture that can be decoded independently.
                write_packet(previous_delta, pts)
                pts += 33333
            for frame_index, picture in enumerate(pictures):
                key_flag = KEY_FRAME if frame_index == 0 and index != 1 else 0
                write_packet(picture, key_flag | pts)
                pts += 33333
            previous_delta = pictures[-1]


if __name__ == "__main__":
    output_dir = Path(sys.argv[1])
    output_dir.mkdir(parents=True, exist_ok=True)
    write_fixture(output_dir / "h265-resize-android-video.bin")
