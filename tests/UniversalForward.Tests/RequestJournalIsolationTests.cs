using System.Reflection;
using System.Runtime.Loader;

namespace UniversalForward.Tests;

[TestClass]
public sealed class RequestJournalIsolationTests
{
    [TestMethod]
    public void PublishedPluginUsesMemoryWithoutSqlite()
    {
        var directory = Environment.GetEnvironmentVariable("UNIVERSALFORWARD_TEST_PACKAGE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            Assert.Inconclusive("需要 UNIVERSALFORWARD_TEST_PACKAGE 指向已构建的插件目录");
            return;
        }
        var entry = Path.Combine(Path.GetFullPath(directory), "Plugins.UniversalForward.dll");
        var context = new IsolatedContext(entry);
        try
        {
            var assembly = context.LoadFromAssemblyPath(entry);
            var type = assembly.GetType("Plugins.UniversalForward.RequestLogStore", true)!;
            using var store = (IDisposable)Activator.CreateInstance(type, [256])!;
            Assert.IsTrue((bool)type.GetProperty("Available")!.GetValue(store)!,
                System.Text.Json.JsonSerializer.Serialize(type.GetProperty("Status")!.GetValue(store)));
            Assert.IsFalse(context.Assemblies.Any(a => a.GetName().Name!.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(Directory.EnumerateFiles(directory, "*sqlite*", SearchOption.AllDirectories).Any());
        }
        finally { context.Unload(); }
    }

    private sealed class IsolatedContext(string entry) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "Router.Contracts") return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(Validate(path));
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? 0 : LoadUnmanagedDllFromPath(Validate(path));
        }
        private static string Validate(string path)
        {
            var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("UNIVERSALFORWARD_TEST_PACKAGE")!);
            Assert.IsTrue(Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), path);
            return path;
        }
    }
}
