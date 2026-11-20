using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SonicRoute
{
    internal static class EmbeddedAudioLibraries
    {
        private static readonly Lazy<Assembly> Core = new(() => Load("NAudio.Core"));
        private static readonly Lazy<Assembly> Wasapi = new(() => Load("NAudio.Wasapi"));

        [ModuleInitializer]
        internal static void Register() => AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            new AssemblyName(args.Name).Name switch
            {
                "NAudio.Core" => Core.Value,
                "NAudio.Wasapi" => Wasapi.Value,
                _ => null
            };

        private static Assembly Load(string name)
        {
            using var resource = typeof(EmbeddedAudioLibraries).Assembly.GetManifestResourceStream(
                "SonicRoute.AudioLibraries." + name + ".dll") ?? throw new FileNotFoundException(name);
            using var data = new MemoryStream((int)resource.Length);
            resource.CopyTo(data);
            return Assembly.Load(data.GetBuffer());
        }
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
