"""Build in a fresh Lab-owned ASCII drive mapping; tool cache/reference are read-only."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from build_server import build_server

work = Path(sys.argv[1])
repo = Path(sys.argv[2])
reference = repo.parent / 'android-audio-test'
cache = Path(os.environ['LOCALAPPDATA']) / 'AndroidAudioTest'
prefix = cache / 'prefix'
toolchain = cache / 'toolchain/llvm-mingw-20261020-ucrt-x86_64/bin'
stage = work / 'src'
build = work / 'build'
native = repo / 'packaging/AndroidLab/native'
env = dict(os.environ)
env['PATH'] = os.pathsep.join([str(toolchain), str(cache / 'python/Scripts'), str(prefix / 'bin'), env.get('PATH', '')])
env['TEMP'] = env['TMP'] = str(work / 'tmp')
env['PYTHONDONTWRITEBYTECODE'] = '1'
Path(env['TEMP']).mkdir()

def run(args, cwd=None):
    print('RUN ' + ' '.join(str(a) for a in args), flush=True)
    subprocess.run([str(a) for a in args], cwd=cwd, env=env, check=True)

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

shutil.copytree(reference / 'third_party/scrcpy-experimental', stage,
                ignore=shutil.ignore_patterns('.git', '__pycache__'))
patches = sorted((reference / 'patches').glob('*.patch'))
for patch in patches:
    run(['git', '-C', stage, 'apply', '--ignore-space-change', patch])
run(['git', '-C', stage, 'apply', '--ignore-space-change', native / 'virtual_resize.patch'])
display_manager = stage / 'server/src/main/java/com/genymobile/scrcpy/wrappers/DisplayManager.java'
manager_text = display_manager.read_text(encoding='utf-8')
create_display = 'return dm.createVirtualDisplay(name, width, height, dpi, surface, flags);'
if manager_text.count(create_display) != 1:
    raise RuntimeError('Owned display creation integration point changed')
display_manager.write_text(manager_text.replace(create_display,
    'return OwnedDisplayPolicy.create(dm, name, width, height, dpi, surface, flags);'), encoding='utf-8')
shutil.copyfile(native / 'OwnedDisplayPolicy.java', display_manager.parent / 'OwnedDisplayPolicy.java')
controller_java = stage / 'server/src/main/java/com/genymobile/scrcpy/control/Controller.java'
controller_text = controller_java.read_text(encoding='utf-8')
for old, new in [
    ('    private ExecutorService startAppExecutor;', '    private ExecutorService startAppExecutor;\n    private OwnedAppMonitor ownedApp;\n    private final boolean ownedDisplay;'),
    ('        this.displayId = options.getDisplayId();', '        this.displayId = options.getDisplayId();\n        this.ownedDisplay = options.getNewDisplay() != null;'),
    ('    public void stop() {\n        if (thread != null)', '    public void stop() {\n        if (ownedApp != null) ownedApp.close();\n        UnicodeInputBridge.closeOwned();\n        if (thread != null)'),
    ('        Device.startApp(app.getPackageName(), startAppDisplayId, forceStopBeforeStart);',
     '        if (ownedDisplay && ownedApp == null) {\n            ownedApp = new OwnedAppMonitor(app.getPackageName(), startAppDisplayId);\n            ownedApp.start();\n        }\n'
     '        if (ownedApp != null) ownedApp.launching();\n'
     '        Device.startApp(app.getPackageName(), startAppDisplayId, forceStopBeforeStart);'),
]:
    if controller_text.count(old) != 1: raise RuntimeError('Owned task integration point changed: ' + old)
    controller_text = controller_text.replace(old, new)
shutil.copyfile(native / 'OwnedAppMonitor.java', controller_java.parent / 'OwnedAppMonitor.java')
shutil.copyfile(native / 'UnicodeInputBridge.java', controller_java.parent / 'UnicodeInputBridge.java')
shutil.copyfile(native / 'PairingControl.java', controller_java.parent / 'PairingControl.java')
inject_method = '    private int injectText(String text) {'
if controller_text.count(inject_method) != 1:
    raise RuntimeError('Unicode text integration point changed')
controller_text = controller_text.replace(inject_method, inject_method + '''
        for (int i = 0; i < text.length(); ++i) {
            if (text.charAt(i) > 0xff) {
                if (ownedApp != null) {
                    int committed = UnicodeInputBridge.commit(getActionDisplayId(), ownedApp.getPackageName(), text);
                    if (committed > 0) return committed;
                    if (committed < 0) return 0; // An interrupted acknowledgement must not duplicate an applied edit.
                }
                // KeyCharacterMap cannot represent a Windows IME's committed Chinese/Unicode text.
                // Use the existing explicit device-paste path, never the desktop clipboard.
                // Wait for key dispatch so another committed chunk cannot replace the clip early.
                // The existing setter returns false for an unchanged clip as well as failure.
                // Verify an unchanged clip instead of dropping repeated identical IME commits.
                if (!setClipboard(text, false, ControlMessage.SEQUENCE_INVALID) && !text.equals(Device.getClipboardText())) return 0;
                return pressReleaseKeycode(KeyEvent.KEYCODE_PASTE, Device.INJECT_MODE_WAIT_FOR_FINISH) ? text.length() : 0;
            }
        }
''')
inject_text = '                    injectText(msg.getText());'
if controller_text.count(inject_text) != 1:
    raise RuntimeError('Desktop keyboard diagnostic integration point changed')
controller_java.write_text(controller_text.replace(inject_text,
    '                    int injected = injectText(msg.getText());\n'
    '                    if (Ln.isEnabled(Ln.Level.DEBUG)) {\n'
    '                        Ln.d("Desktop text dispatch: display=" + getActionDisplayId() + ", dispatched=" + injected);\n'
    '                    }'), encoding='utf-8')
screen = stage / 'app/src/screen.c'
text = screen.read_text(encoding='utf-8')
for old, new in [
    ('#include "screen.h"', '#include "screen.h"\n#include "flex_resize.h"\n#include "resize_packet.h"'),
    ('    struct sc_screen *screen = data;\n    assert(screen->video);',
     '    struct sc_screen *screen = data;\n    if (sr_flex_enabled()) return 0;\n    assert(screen->video);'),
    ('    SDL_ShowWindow(screen->window);', '    sr_flex_install(screen);\n    SDL_ShowWindow(screen->window);'),
    ('    SDL_DestroyWindow(screen->window);', '    sr_flex_uninstall(screen);\n    SDL_DestroyWindow(screen->window);'),
    ('    SDL_Rect *rect = &screen->rect;', '    SDL_Rect *rect = &screen->rect;\n    if (sr_flex_enabled()) { *rect = (SDL_Rect) {0, 0, dw, dh}; return; }'),
    ('    if (!screen->fullscreen && !screen->maximized && !screen->minimized) {\n        resize_for_content',
     '    if (sr_flex_enabled()) { screen->content_size = new_content_size; return; }\n    if (!screen->fullscreen && !screen->maximized && !screen->minimized) {\n        resize_for_content')
]:
    expected = 2 if old.startswith('    SDL_DestroyWindow') else 1
    if text.count(old) != expected:
        raise RuntimeError('Native integration point changed: ' + old)
    text = text.replace(old, new)
text += '''
#ifdef _WIN32
_Static_assert(SC_CONTROL_MSG_TYPE_RESIZE_DISPLAY == SR_RESIZE_CONTROL_TYPE, "Resize protocol mismatch");
static void sr_flex_request(struct sc_screen *screen, LONG width, LONG height) {
    if (!screen->im.controller) return;
    struct sc_control_msg msg = { .type = SC_CONTROL_MSG_TYPE_RESIZE_DISPLAY,
        .resize_display = { (uint16_t)width, (uint16_t)height } };
    sc_controller_push_msg(screen->im.controller, &msg);
}
static void sr_flex_present(struct sc_screen *screen) {
    if (!screen->video || !screen->has_frame) return;
    SDL_Event event;
    if (SDL_PeepEvents(&event, 1, SDL_GETEVENT, SC_EVENT_NEW_FRAME, SC_EVENT_NEW_FRAME) == 1) {
        sc_screen_update_content_rect(screen);
        sc_screen_handle_event(screen, &event);
    } else sc_screen_render(screen, true);
}
static void sr_flex_restore(struct sc_screen *screen) {
    const char *package = SDL_getenv("SONICROUTE_APP_PACKAGE");
    if (!screen->im.controller || !package) return;
    // The controller owns this string and destroys it with the executable's free().
    // SDL's DLL allocator is a different heap on Windows and must not cross that boundary.
    struct sc_control_msg msg = { .type = SC_CONTROL_MSG_TYPE_START_APP,
        .start_app = { .name = strdup(package) } };
    if (msg.start_app.name && !sc_controller_push_msg(screen->im.controller, &msg)) free(msg.start_app.name);
}
static void sr_flex_window_size(LONG width, LONG height) {
    LOGI("SonicRoute window-size: %ldx%ld", (long) width, (long) height);
}
#endif
'''
screen.write_text(text, encoding='utf-8')
shutil.copyfile(native / 'flex_resize.h', stage / 'app/src/flex_resize.h')
shutil.copyfile(native / 'ime_routing.h', stage / 'app/src/ime_routing.h')
shutil.copyfile(native / 'resize_packet.h', stage / 'app/src/resize_packet.h')
input_header = stage / 'app/src/input_manager.h'
input_header_text = input_header.read_text(encoding='utf-8')
input_header.write_text(input_header_text.replace('struct sc_input_manager {',
    '#include "ime_routing.h"\nstruct sc_input_manager {\n    struct sr_ime_route host_ime;'), encoding='utf-8')
input_manager = stage / 'app/src/input_manager.c'
input_text = input_manager.read_text(encoding='utf-8')
handle_event = '                              const SDL_Event *event) {\n    bool control = im->controller;'
if input_text.count(handle_event) != 1: raise RuntimeError('SDL input routing integration point changed')
input_manager.write_text(input_text.replace(handle_event,
    '                              const SDL_Event *event) {\n    if (sr_ime_route_event(&im->host_ime, event)) return;\n    bool control = im->controller;'), encoding='utf-8')
capture = stage / 'server/src/main/java/com/genymobile/scrcpy/video/NewDisplayCapture.java'
capture_text = capture.read_text(encoding='utf-8')
for old, new in [
    ('if (displaySize == null || dpi == 0) {', 'if (mainDisplaySize == null) {'),
    ('            if (!newDisplay.hasExplicitDpi()) {',
     '            displaySize = PhoneDisplaySizing.fit(mainDisplaySize, displaySize.getWidth(), displaySize.getHeight());\n'
     '            if (!newDisplay.hasExplicitDpi()) {'),
    ('        synchronized (resizeLock) {\n            VirtualDisplay owned = virtualDisplay;',
     '        Size fitted = PhoneDisplaySizing.fit(mainDisplaySize, width, height);\n'
     '        width = fitted.getWidth(); height = fitted.getHeight();\n'
     '        synchronized (resizeLock) {\n            VirtualDisplay owned = virtualDisplay;')
]:
    if capture_text.count(old) != 1:
        raise RuntimeError('Phone resolution integration point changed: ' + old)
    capture_text = capture_text.replace(old, new)
capture.write_text(capture_text, encoding='utf-8')
shutil.copyfile(native / 'PhoneDisplaySizing.java', capture.parent / 'PhoneDisplaySizing.java')
ini = work / 'native.ini'
ini.write_text('[binaries]\n' + '\n'.join(
    f"{key} = '{(toolchain / exe).as_posix()}'" for key, exe in [
        ('c', 'x86_64-w64-mingw32-clang.exe'), ('ar', 'x86_64-w64-mingw32-ar.exe'),
        ('strip', 'llvm-strip.exe'), ('windres', 'x86_64-w64-mingw32-windres.exe')]) + '\n', encoding='utf-8')
meson = [sys.executable, '-B', '-m', 'mesonbuild.mesonmain']
run(meson + ['setup', '--native-file', ini, '--buildtype=release', '-Dprebuilt_deps=' + str(prefix),
             '-Dusb=false', '-Dv4l2=false', '-Dcompile_server=false', '-Dportable=true',
             '--prefix', work / 'install', build, stage])
run(meson + ['compile', '-C', build])
check = work / 'flex-check.exe'
run([toolchain / 'x86_64-w64-mingw32-clang.exe', '-std=c11', '-Wall', '-Wextra',
     '-I' + str(prefix / 'include'), '-I' + str(native), native / 'flex_check.c',
     '-L' + str(prefix / 'lib'), '-lSDL2', '-luser32', '-o', check])
packets = work / 'resize-packets.bin'
run([check, packets])
video_server, server_receipt = build_server(work, repo, stage, run, packets, env)
output = repo.parent / 'outputs/video-client'
output.mkdir(exist_ok=True)
target = output / 'scrcpy.exe'
shutil.copyfile(build / 'app/scrcpy.exe', target)
server_target = output / video_server.name
shutil.copyfile(video_server, server_target)
receipt = dict(Executable=str(target), Bytes=target.stat().st_size, SHA256=sha(target),
               Server=dict(Name=server_target.name, Bytes=server_target.stat().st_size, SHA256=sha(server_target), **server_receipt),
               ReferencePatches=[dict(Name=p.name, SHA256=sha(p)) for p in patches],
               NativeSources=[dict(Name=p.name, SHA256=sha(p)) for p in native.iterdir() if p.is_file()],
               FlexCheck='8 unconstrained edges, intermediate/final control packets, bounds/deduplication, UI-thread presentation and hook cleanup; hidden HWND, no phone')
(output / 'build-receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
print('READY ' + str(target) + ' ' + receipt['SHA256'], flush=True)
