using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Deviant.Catalog;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length is not (5 or 6)) return 2;
        var assemblies = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            var path = Path.Combine(assemblies, new AssemblyName(request.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        return Hold(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Hold(string[] args)
    {
        using var cache = new CacheManager(Path.Combine(args[1], "worker-settings"),
            new CacheSettings(Path.Combine(args[1], "cache"), long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)));
        Publish(args[3], typeof(CacheManager).Assembly.Location);
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(args[4]) && elapsed.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(20);
        if (args.Length == 6 && args[5] == "swap" && File.Exists(args[4]))
        {
            var budget = CacheStorageBudget.ForRoot(cache.Location);
            var first = Path.Combine(cache.Location, "first.data");
            using (var deletion = budget.Acquire(first, 192 * 1024, 0))
            {
                if (deletion is null) return 3;
                File.Delete(first); deletion.Complete(0);
            }
            var second = Path.Combine(cache.Location, "second.data");
            using (var creation = budget.Acquire(second, 0, 192 * 1024))
            {
                if (creation is null) return 4;
                File.WriteAllBytes(second, new byte[192 * 1024]); creation.Complete(192 * 1024);
            }
            Publish(args[3] + ".swapped", "ready");
            while (!File.Exists(args[4] + ".exit") && elapsed.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(20);
        }
        return 0;
    }

    private static void Publish(string path, string value)
    {
        var pending = path + ".pending";
        File.WriteAllText(pending, value);
        File.Move(pending, path);
    }
}
