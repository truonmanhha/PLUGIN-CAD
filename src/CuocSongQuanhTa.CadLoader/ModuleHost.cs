using System.Reflection;
using System.Runtime.Loader;

namespace CuocSongQuanhTa.CadLoader;

internal static class ModuleHost
{
    private const string ModuleFileName = "CuocSongQuanhTa.CadTools.Module.dll";
    private static readonly object SyncRoot = new();
    private static ICadToolsModule? _module;
    private static ModuleLoadContext? _loadContext;

    public static bool TryGetModule(out ICadToolsModule? module, out string error)
    {
        lock (SyncRoot)
        {
            if (_module is null && !TryLoadCore(out error))
            {
                module = null;
                return false;
            }

            module = _module;
            error = string.Empty;
            return module is not null;
        }
    }

    public static bool TryLoad(out string error)
    {
        lock (SyncRoot)
        {
            if (_module is not null)
            {
                error = string.Empty;
                return true;
            }

            return TryLoadCore(out error);
        }
    }

    public static bool Reload(out string error)
    {
        lock (SyncRoot)
        {
            UnloadCore();
            return TryLoadCore(out error);
        }
    }

    public static void Unload()
    {
        lock (SyncRoot)
        {
            UnloadCore();
        }
    }

    private static bool TryLoadCore(out string error)
    {
        try
        {
            var modulePath = GetModulePath();
            if (!File.Exists(modulePath))
            {
                error = $"Khong tim thay module: {modulePath}";
                return false;
            }

            var context = new ModuleLoadContext(Path.GetDirectoryName(modulePath)!);
            using var assemblyStream = new MemoryStream(File.ReadAllBytes(modulePath));
            var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
            Assembly assembly;
            if (File.Exists(pdbPath))
            {
                using var pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));
                assembly = context.LoadFromStream(assemblyStream, pdbStream);
            }
            else
            {
                assembly = context.LoadFromStream(assemblyStream);
            }

            var moduleType = assembly.GetTypes().FirstOrDefault(type =>
                !type.IsAbstract && typeof(ICadToolsModule).IsAssignableFrom(type));
            if (moduleType is null || Activator.CreateInstance(moduleType) is not ICadToolsModule module)
            {
                context.Unload();
                error = "Module khong co diem khoi tao hop le.";
                return false;
            }

            _loadContext = context;
            _module = module;
            error = string.Empty;
            return true;
        }
        catch (System.Exception exception)
        {
            _module = null;
            _loadContext?.Unload();
            _loadContext = null;
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static void UnloadCore()
    {
        try
        {
            _module?.Dispose();
        }
        finally
        {
            _module = null;
            _loadContext?.Unload();
            _loadContext = null;
        }
    }

    private static string GetModulePath()
    {
        var loaderDirectory = Path.GetDirectoryName(typeof(ModuleHost).Assembly.Location)
            ?? throw new InvalidOperationException("Khong xac dinh duoc thu muc Loader.");
        return Path.Combine(loaderDirectory, "Modules", ModuleFileName);
    }

    private sealed class ModuleLoadContext(string moduleDirectory) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _moduleDirectory = moduleDirectory;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sharedAssembly = Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    assemblyName.Name,
                    StringComparison.OrdinalIgnoreCase));
            if (sharedAssembly is not null)
            {
                return sharedAssembly;
            }

            var dependencyPath = Path.Combine(_moduleDirectory, $"{assemblyName.Name}.dll");
            if (!File.Exists(dependencyPath))
            {
                return null;
            }

            using var stream = new MemoryStream(File.ReadAllBytes(dependencyPath));
            return LoadFromStream(stream);
        }
    }
}
