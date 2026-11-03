using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using AdServerGroups.Abstractions;

namespace AdServerGroups.Services;

/// <summary>
/// Грузит опциональные DLL из папки Providers рядом с exe.
/// Нет папки / нет DLL — провайдеров нет, вкладка SQL скрыта.
/// </summary>
public static class ProviderLoader
{
    public const string ProvidersFolderName = "Providers";

    public static string ProvidersDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            var dir = !string.IsNullOrWhiteSpace(processPath)
                ? Path.GetDirectoryName(processPath)
                : AppContext.BaseDirectory;
            dir ??= AppContext.BaseDirectory;
            return Path.Combine(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), ProvidersFolderName);
        }
    }

    public static IReadOnlyList<ISqlSysadminProvider> LoadSqlProviders()
    {
        var list = new List<ISqlSysadminProvider>();
        var dir = ProvidersDirectory;
        if (!Directory.Exists(dir))
            return list;

        foreach (var dll in Directory.EnumerateFiles(dir, "AdServerGroups.*Provider.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var alc = new PluginLoadContext(dll);
                var asm = alc.LoadFromAssemblyPath(dll);
                foreach (var type in asm.GetExportedTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!typeof(ISqlSysadminProvider).IsAssignableFrom(type)) continue;
                    if (Activator.CreateInstance(type) is ISqlSysadminProvider provider)
                        list.Add(provider);
                }
            }
            catch
            {
                // битый/несовместимый провайдер — пропускаем
            }
        }

        return list;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Abstractions уже в host — не грузить вторую копию из Providers
            if (string.Equals(assemblyName.Name, "AdServerGroups.Abstractions", StringComparison.OrdinalIgnoreCase))
                return null;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
