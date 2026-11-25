"""Read back the four published PE32+ products, runtime asset graphs and publish logs."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct

def inspect(exe):
    data = exe.read_bytes()
    def u16(i): return struct.unpack_from("<H", data, i)[0]
    def u32(i): return struct.unpack_from("<I", data, i)[0]
    pe = u32(0x3c)
    assert data[:2] == b"MZ" and data[pe:pe+4] == b"PE\0\0"
    machine, count = u16(pe+4), u16(pe+6)
    opt = pe+24
    assert u16(opt) == 0x20b
    section = opt + u16(pe+20)
    def offset(rva):
        for n in range(count):
            base = section + n*40
            va = u32(base+12)
            if va <= rva < va + u32(base+16):
                return u32(base+20) + rva - va
        raise ValueError(f"Unmapped RVA {rva:#x}")
    def dll(rva):
        start = offset(rva)
        return data[start:data.index(0, start)].decode("ascii")
    imports = []
    descriptors = u32(opt+112+8)
    if descriptors:
        at = offset(descriptors)
        while u32(at+12):
            imports.append(dll(u32(at+12)))
            at += 20
    delayed = []
    desc = u32(opt+112+13*8)
    if desc:
        at = offset(desc)
        while u32(at+4):
            assert u32(at) & 1
            delayed.append(dll(u32(at+4)))
            at += 32
    clr = struct.unpack_from("<II", data, opt+112+14*8)
    assert clr == (0, 0) and u16(opt+68) == 2
    assert not set(x.lower() for x in imports+delayed) & {"mscoree.dll","coreclr.dll","clrjit.dll","hostfxr.dll","hostpolicy.dll"}
    return dict(bytes=len(data), sha256=hashlib.sha256(data).hexdigest(), machine=hex(machine), pe="PE32+",
                subsystem="Windows GUI", clrDirectory=clr, imports=sorted(imports), delayImports=sorted(delayed))

if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("output_root", type=Path)
    args = p.parse_args()
    result = {}
    for edition in ("Full", "Lite"):
        for arch, expected_machine in (("x64", "0x8664"), ("arm64", "0xaa64")):
            variant = edition + "-" + arch
            published = args.output_root/"candidate"/variant
            info = inspect(published/"SonicRoute.exe")
            assert info["machine"] == expected_machine
            assets = json.loads((args.output_root/"build"/variant/"obj/Product/project.assets.json").read_text(encoding="utf-8-sig"))
            graph = sorted(assets["libraries"])
            assert not any(re.match(r"(NAudio|System.Management/|Microsoft.WindowsDesktop.App)", x, re.I) for x in graph)
            log = (published/"publish.log").read_text(encoding="utf-8-sig")
            assert not re.search(r"\bIL[23]\d{3}\s*:", log)
            info["assetGraph"] = graph
            info["trimOrAotWarningsInPublishLog"] = 0
            result[variant] = info
    out = args.output_root / "evidence" / "artifacts.json"
    out.write_text(json.dumps(result, indent=2), encoding="utf-8")
