"""Minimal shared FFmpeg for video/input windows; all writes stay in a fresh Lab stage."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import urllib.request
import zipfile

work = Path(sys.argv[1])
repo = Path(sys.argv[2])
cache = Path(os.environ['LOCALAPPDATA']) / 'AndroidAudioTest'
tc = cache / 'toolchain/llvm-mingw-20261020-ucrt-x86_64'
prefix = work / 'prefix'
for part in ['include', 'lib', 'bin']:
    (prefix / part).mkdir(parents=True)
env = dict(os.environ)
env['PATH'] = os.pathsep.join([str(tc / 'bin'), str(tc / 'busybox/bin'), env.get('PATH', '')])
env['TEMP'] = env['TMP'] = env['TMPDIR'] = (work / 'tmp').as_posix()
Path(env['TEMP']).mkdir()
shell = tc / 'busybox/bin/sh.exe'
env['SHELL'] = env['MAKESHELL'] = shell.as_posix()

def run(args, cwd=None):
    print('RUN ' + ' '.join(str(a) for a in args), flush=True)
    subprocess.run([str(a) for a in args], cwd=cwd, env=env, check=True)

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

inputs = []
def archive(name, url, expected=None):
    path = work / name
    cached = next((p for p in (repo.parent / 'outputs').glob('runtime-build-*/' + name)
                   if p.stat().st_size > 0 and expected and sha(p) == expected.upper()), None)
    if cached:
        print('REUSE verified archive ' + name, flush=True)
        shutil.copyfile(cached, path)
    else:
        print('DOWNLOAD ' + url, flush=True)
        with urllib.request.urlopen(url, timeout=90) as response, path.open('wb') as target:
            shutil.copyfileobj(response, target)
    digest = sha(path)
    if expected and digest != expected.upper():
        raise RuntimeError('Source archive hash differs: ' + name)
    inputs.append(dict(Name=name, URL=url, SHA256=digest))
    dest = work / (name.split('.')[0] + '-source')
    dest.mkdir()
    if name.endswith('.zip'):
        with zipfile.ZipFile(path) as z:
            for info in z.infolist():
                resolved = (dest / info.filename).resolve()
                if not resolved.is_relative_to(dest.resolve()):
                    raise RuntimeError('Archive path escapes stage')
            z.extractall(dest)
    else:
        with tarfile.open(path) as t:
            t.extractall(dest, filter='data')
    return next(p for p in dest.iterdir() if p.is_dir())

ffmpeg = archive('ffmpeg-8.1.tar.gz', 'https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/refs/tags/n8.1',
                 'DD308201BB1239A1B73185F80C6B4121F4EFDFA424A009CE544FD00BF736BB2E')
zlib = archive('zlib-1.3.2.tar.gz', 'https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.gz',
               'bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16')
nasm = archive('nasm-2.16.03.zip', 'https://www.nasm.us/pub/nasm/releasebuilds/2.16.03/win64/nasm-2.16.03-win64.zip',
               '3EE4782247BCB874378D02F7EAB4E294A84D3D15F3F6EE2DE2F47A46AA7226E6')
env['PATH'] = str(nasm) + os.pathsep + env['PATH']

# Only the zlib core is needed by the PNG decoder, not its programs or gz* wrappers.
zbuild = work / 'zlib-build'
zbuild.mkdir()
zobjects = []
for name in ['adler32', 'crc32', 'deflate', 'infback', 'inffast', 'inflate', 'inftrees', 'trees', 'zutil', 'compress', 'uncompr']:
    obj = zbuild / (name + '.o')
    run([tc / 'bin/x86_64-w64-mingw32-clang.exe', '-O2', '-DNDEBUG', '-I' + str(zlib), '-c', zlib / (name + '.c'), '-o', obj])
    zobjects.append(obj)
run([tc / 'bin/llvm-ar.exe', 'rcs', prefix / 'lib/libz.a'] + zobjects)
for name in ['zlib.h', 'zconf.h']:
    shutil.copyfile(zlib / name, prefix / 'include' / name)

build = work / 'ffmpeg-build'
build.mkdir()
options = [
    '--prefix=' + prefix.as_posix(), '--target-os=mingw32', '--arch=x86_64', '--enable-cross-compile',
    '--cc=x86_64-w64-mingw32-clang', '--cxx=x86_64-w64-mingw32-clang++',
    '--ar=llvm-ar', '--ranlib=llvm-ranlib', '--nm=llvm-nm', '--strip=llvm-strip', '--windres=x86_64-w64-mingw32-windres',
    '--x86asmexe=' + (nasm / 'nasm.exe').as_posix(), '--ln_s=cp',
    '--disable-everything', '--disable-autodetect', '--disable-programs', '--disable-doc', '--disable-debug',
    '--disable-network', '--disable-avdevice', '--disable-avfilter', '--disable-swscale',
    '--enable-shared', '--disable-static', '--enable-swresample', '--enable-zlib',
    '--enable-decoder=h264,hevc,png', '--enable-parser=h264,hevc', '--enable-demuxer=image2,png_pipe', '--enable-protocol=file',
    '--extra-cflags=-I' + (prefix / 'include').as_posix(), '--extra-ldflags=-L' + (prefix / 'lib').as_posix()
]
run([shell, (ffmpeg / 'configure').as_posix()] + options, build)
run([tc / 'bin/mingw32-make.exe', '-j8'], build)
run([tc / 'bin/mingw32-make.exe', 'install'], build)

output = repo.parent / 'outputs/video-runtime'
output.mkdir(exist_ok=True)
names = ['avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll']
files = []
for name in names:
    target = output / name
    shutil.copyfile(prefix / 'bin' / name, target)
    files.append(dict(Name=name, Bytes=target.stat().st_size, SHA256=sha(target)))
for source, name in [(ffmpeg / 'COPYING.LGPLv2.1', 'FFmpeg-LICENSE.txt'), (zlib / 'LICENSE', 'zlib-LICENSE.txt')]:
    shutil.copyfile(source, output / name)
receipt = dict(Sources=inputs, Configure=options, Files=files, Bytes=sum(f['Bytes'] for f in files),
               Note='Video H264/HEVC and PNG only; native audio remains in-process Concentus/NAudio. No general multimedia tools.')
(output / 'build-receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
print('READY video runtime bytes=' + str(receipt['Bytes']), flush=True)
