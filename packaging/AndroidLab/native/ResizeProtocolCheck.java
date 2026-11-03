import com.genymobile.scrcpy.control.ControlMessage;
import com.genymobile.scrcpy.control.ControlMessageReader;
import com.genymobile.scrcpy.control.ControlProtocolException;
import java.io.*;
import java.nio.file.*;

// Host-only check of the real Android reader against packets emitted by the native HWND fixture.
public final class ResizeProtocolCheck {
    private static void check(boolean condition) { if (!condition) throw new AssertionError(); }
    private static byte[] packet(int width, int height) {
        return new byte[] {18, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height};
    }
    public static void main(String[] args) throws Exception {
        byte[] packets = Files.readAllBytes(Paths.get(args[0]));
        check(packets.length == 60);
        ControlMessageReader reader = new ControlMessageReader(new ByteArrayInputStream(packets));
        for (int i = 0; i < 12; ++i) {
            ControlMessage message = reader.read();
            check(message.getType() == ControlMessage.TYPE_RESIZE_DISPLAY);
            int w = message.getResizeWidth(), h = message.getResizeHeight();
            check(w >= 240 && w <= 8192 && h >= 240 && h <= 8192 && ((w | h) & 7) == 0);
            if (i == 8) check(w == 640 && h == 360);
            if (i == 9) check(w == 8192 && h == 240);
            if (i == 10) check(w == 1000 && h == 600);
            if (i == 11) check(w == 1000 && h == 608); // Exact 1003x613 window size is separate from the aligned VD request.
        }
        for (int[] size : new int[][] {{0, 600}, {1001, 600}, {8193, 600}, {800, 239}}) {
            try { new ControlMessageReader(new ByteArrayInputStream(packet(size[0], size[1]))).read(); throw new AssertionError(); }
            catch (ControlProtocolException expected) { }
        }
        try { new ControlMessageReader(new ByteArrayInputStream(new byte[] {18, 3, 32})).read(); throw new AssertionError(); }
        catch (EOFException expected) { }
        check(new ControlMessageReader(new ByteArrayInputStream(new byte[] {17})).read().getType() == ControlMessage.TYPE_RESET_VIDEO);
        System.out.println("PASS native-to-Java resize packets, bounds/alignment/truncation and original reset-video command; no Android device");
    }
}
