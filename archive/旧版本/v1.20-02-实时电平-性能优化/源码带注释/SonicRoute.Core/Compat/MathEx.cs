using System;

namespace SonicRoute.Core.Compat
{
    /// <summary>
    /// Math.Clamp 的最小兼容垫片。
    ///
    /// 原因：Math.Clamp 是 .NET Standard 2.1 / .NET Core 2.0+ 引入的 API，
    /// .NET Framework 4.8 的 mscorlib 不含该方法（已核对 net471 参考程序集）。
    ///
    /// 语义与 System.Math.Clamp 完全一致，包括边界行为：
    ///   - value &lt; min → 返回 min；value &gt; max → 返回 max；否则返回 value
    ///   - min &gt; max 时返回 min（与 BCL 行为一致）
    ///   - double.NaN 参与比较恒为 false，最终返回 NaN（与 BCL 行为一致）
    ///
    /// 两个 TFM 共用同一实现（net8.0-windows 也走这里），保证单一代码路径与行为一致。
    ///
    /// 可见性为 public：SonicRoute（net8）与 SonicRoute.Legacy（net48）都通过 ProjectReference
    /// 引用本程序集，共享源码中的调用点位于别的程序集内，internal 会报 CS0122。
    /// </summary>
    public static class MathEx
    {
        public static int Clamp(int value, int min, int max) =>
            value < min ? min : (value > max ? max : value);

        public static long Clamp(long value, long min, long max) =>
            value < min ? min : (value > max ? max : value);

        public static float Clamp(float value, float min, float max) =>
            value < min ? min : (value > max ? max : value);

        public static double Clamp(double value, double min, double max) =>
            value < min ? min : (value > max ? max : value);

        public static decimal Clamp(decimal value, decimal min, decimal max) =>
            value < min ? min : (value > max ? max : value);
    }
}
