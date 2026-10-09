#if NET48
// =====================================================================
// net48 编译器特性垫片（最小兼容垫片，不参与任何业务逻辑）
//
// 作用：让 C# 9 的 init 访问器与 C# 11 的 required 成员语法在 .NET Framework 4.8 上编译通过。
// 这些类型由 .NET 5+ 的 BCL 提供（System.Runtime / System.Runtime.CompilerServices），net48 缺失。
//
// 可见性必须是 public（而非 internal）：
//   编译器查找这些"well-known 特性"时会搜索引用程序集，但要求从使用点可访问。
//   SonicRoute（net8）与 SonicRoute.Legacy（net48）都通过 ProjectReference 引用本程序集，
//   Legacy 分支链接的共享源码里含 required / init（AppItem.cs、L10n.cs 等），
//   若声明为 internal 则 Legacy 编译报 CS0656（缺少编译器所需的成员）。
//   因此这里 public 暴露，仅在 net48 目标下存在。
//
// 说明：本文件仅在 net48 目标下参与编译（net8.0-windows 分支由 BCL 提供同名类型，
// 若不加条件会导致 CS0433「类型同时存在于两个程序集」）。
// =====================================================================

namespace System.Runtime.CompilerServices
{
    /// <summary>C# 9 init 访问器所需的编译器标记类型（net48 缺失）。</summary>
    public static class IsExternalInit
    {
    }

    /// <summary>C# 11 required 成员所需的标记特性（net48 缺失）。</summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>C# 11 编译器功能标记（required / ref struct 等），net48 缺失。</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    public sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; set; }

        public const string RefStructs = nameof(RefStructs);

        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>构造函数已设置全部 required 成员时使用的标记特性（net48 缺失）。</summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    public sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
#endif
