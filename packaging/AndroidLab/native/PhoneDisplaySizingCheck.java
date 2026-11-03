import com.genymobile.scrcpy.device.Size;
import com.genymobile.scrcpy.video.PhoneDisplaySizing;

public final class PhoneDisplaySizingCheck {
    private static void check(boolean condition) { if (!condition) throw new AssertionError(); }
    private static void expected(Size phone, int w, int h, int ew, int eh) {
        Size result = PhoneDisplaySizing.fit(phone, w, h);
        check(result.getWidth() == ew && result.getHeight() == eh);
    }
    public static void main(String[] args) {
        Size phone = new Size(1080, 2400);
        expected(phone, 2560, 1440, 1920, 1080);
        expected(phone, 640, 360, 1920, 1080);
        expected(phone, 800, 600, 1440, 1080);
        expected(phone, 800, 800, 1080, 1080);
        expected(phone, 720, 1280, 1080, 1920);
        expected(phone, 1080, 2400, 1080, 2400);
        expected(new Size(2400,1080), 2560, 1440, 1920, 1080);
        for (int[] shape : new int[][] {{700,850}, {8192,240}, {240,8192}, {1200,680}, {1,1}}) {
            Size result = PhoneDisplaySizing.fit(phone, shape[0], shape[1]);
            check(result.getWidth() >= 240 && result.getHeight() >= 240);
            check((result.getWidth() & 7) == 0 && (result.getHeight() & 7) == 0);
            check((long)result.getWidth() * result.getHeight() <= 1080L * 2400);
        }
        try { PhoneDisplaySizing.fit(phone, 0, 100); throw new AssertionError(); }
        catch (IllegalArgumentException expected) { }
        System.out.println("PASS phone pixel budget, window aspect, orientation, alignment, repeated shape and bounds");
    }
}
