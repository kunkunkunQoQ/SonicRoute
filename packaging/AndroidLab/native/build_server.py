"""Build the video-only control server with pinned local SDK inputs, never touch the audio server."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import urllib.request
import zipfile

SDK_URL = 'https://dl.google.com/android/repository/build-tools_r36_windows.zip'
SDK_SHA256 = 'aa1095cb14d83e483818a748a2c06faaeb8e601561b06a356a119a1b2ca280d3'

def build_server(work, repo, stage, run, packets, env):
    tools = repo.parent / 'outputs/native-sdk-tools'
    tools.mkdir(exist_ok=True)
    archive = tools / 'build-tools_r36_windows.zip'
    if not archive.exists():
        with urllib.request.urlopen(SDK_URL, timeout=60) as response:
            data = response.read(60 * 1024 * 1024)
        if hashlib.sha256(data).hexdigest() != SDK_SHA256:
            raise RuntimeError('Android build-tools checksum mismatch')
        archive.write_bytes(data)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != SDK_SHA256:
        raise RuntimeError('Cached Android build-tools checksum mismatch')
    # Select exact paths: other SDK subfolders contain different DLLs with the same basename.
    inputs = []
    with zipfile.ZipFile(archive) as sdk:
        for name in ['aidl.exe', 'libwinpthread-1.dll', 'core-lambda-stubs.jar']:
            target = tools / name
            data = sdk.read('android-16/' + name)
            target.write_bytes(data)
            inputs.append(dict(Name='android-16/' + name, SHA256=hashlib.sha256(data).hexdigest()))
    (tools / 'receipt.json').write_text(json.dumps(dict(URL=SDK_URL, SHA256=SDK_SHA256, Files=inputs), indent=2), encoding='utf-8')
    sdk_root = repo.parent / 'android-audio-test/tools/sdk'
    android_jar = sdk_root / 'platforms/android-36/android.jar'
    d8 = sdk_root / 'build-tools/36.0.0/lib/d8.jar'
    jdk = Path(os.environ.get('JAVA_HOME', r'C:\Program Files\BellSoft\LibericaJDK-21')) / 'bin'
    server = work / 'server'
    generated, classes = server / 'gen', server / 'classes'
    (generated / 'com/genymobile/scrcpy').mkdir(parents=True)
    classes.mkdir()
    (generated / 'com/genymobile/scrcpy/BuildConfig.java').write_text(
        'package com.genymobile.scrcpy; public final class BuildConfig { '
        'public static final boolean DEBUG=false; public static final String VERSION_NAME="3.3.4"; }', encoding='utf-8')
    framework = server / 'framework.aidl'
    framework.write_text('parcelable android.graphics.Rect;\nparcelable android.content.res.Configuration;\n', encoding='utf-8')
    aidl_src = stage / 'server/src/main/aidl'
    for source in ['android/content/IOnPrimaryClipChangedListener.aidl', 'android/view/IDisplayWindowListener.aidl']:
        # Windows AIDL compares literal import roots: use consistent native separators.
        run([tools / 'aidl.exe', '--omit_invocation', '--out=' + str(generated), '--include=' + str(aidl_src),
             '--preprocessed=' + str(framework), str(aidl_src / source)], cwd=aidl_src)
    java_src = stage / 'server/src/main/java'
    sources = sorted(java_src.rglob('*.java')) + sorted(generated.rglob('*.java'))
    javac_args = server / 'javac.args'
    # javac argument files preserve spaces and avoid the Windows command length limit.
    arguments = ['-encoding', 'UTF-8', '-bootclasspath', str(android_jar), '-classpath', str(tools / 'core-lambda-stubs.jar') + os.pathsep + str(generated),
                 '-source', '8', '-target', '8', '-d', str(classes)] + [str(p) for p in sources]
    javac_args.write_text('\n'.join('"' + a.replace('\\', '/') + '"' for a in arguments), encoding='utf-8')
    run([jdk / 'javac.exe', '@' + str(javac_args)])
    # Run the production reader on the host. It uses no Android service or device.
    checks = server / 'checks'; checks.mkdir()
    protocol_check = repo / 'packaging/AndroidLab/native/ResizeProtocolCheck.java'
    run([jdk / 'javac.exe', '-encoding', 'UTF-8', '-cp', str(classes) + os.pathsep + str(android_jar), '-d', checks, protocol_check])
    run([jdk / 'java.exe', '-cp', os.pathsep.join(map(str, [checks, classes, android_jar])), 'ResizeProtocolCheck', packets])
    sizing_check = repo / 'packaging/AndroidLab/native/PhoneDisplaySizingCheck.java'
    run([jdk / 'javac.exe', '-encoding', 'UTF-8', '-cp', str(classes) + os.pathsep + str(android_jar), '-d', checks, sizing_check])
    run([jdk / 'java.exe', '-cp', os.pathsep.join(map(str, [checks, classes, android_jar])), 'PhoneDisplaySizingCheck'])
    # Keep a bytecode receipt of the production virtual-display path, including VirtualDisplay.resize.
    bytecode = subprocess.check_output([str(jdk / 'javap.exe'), '-c', '-p', '-classpath', str(classes),
        'com.genymobile.scrcpy.video.NewDisplayCapture'], env=env, text=True)
    if 'android/hardware/display/VirtualDisplay.resize:(III)V' not in bytecode:
        raise RuntimeError('Video server does not resize the owned VirtualDisplay')
    (server / 'NewDisplayCapture.bytecode.txt').write_text(bytecode, encoding='utf-8')
    class_jar = server / 'classes.jar'
    run([jdk / 'jar.exe', '--create', '--file', class_jar, '-C', classes, '.'])
    output = server / 'scrcpy-server-video'
    # D8 selects archive output by extension. Rename the built archive only after success.
    dex = server / 'video.zip'
    run([jdk / 'java.exe', '-cp', d8, 'com.android.tools.r8.D8', '--release', '--min-api', '21',
         '--lib', android_jar, '--output', dex, class_jar])
    dex.rename(output)
    with zipfile.ZipFile(output) as dex_archive:
        if 'classes.dex' not in dex_archive.namelist(): raise RuntimeError('Missing video server dex')
    return output, dict(BuildToolsSHA256=SDK_SHA256,
        AndroidJarSHA256=hashlib.sha256(android_jar.read_bytes()).hexdigest(), D8SHA256=hashlib.sha256(d8.read_bytes()).hexdigest(),
        Checks='Real Java protocol reader accepts native HWND packets; production bytecode calls owned VirtualDisplay.resize; no phone')
