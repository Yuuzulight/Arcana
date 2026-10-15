namespace Arcana.Runtime;

public static class EngineLocator
{
    public const string Version = "0.2.4";

    public static string Resolve(string? executable = null)
    {
        executable ??= Environment.GetEnvironmentVariable("ARCANA_ENGINE");
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var fullPath = Path.GetFullPath(executable);
            return File.Exists(fullPath) ? fullPath : throw new FileNotFoundException("Arcana engine was not found.", fullPath);
        }

        var names = OperatingSystem.IsWindows() ? new[] { "arcana-engine.exe", "obscura.exe" } : new[] { "arcana-engine", "obscura" };
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                foreach (var subdirectory in new[] { "engines/obscura/current", "engines/obscura", "engines", "" })
                    foreach (var name in names)
                    {
                        var path = Path.Combine(directory.FullName, subdirectory, name);
                        if (File.Exists(path)) return path;
                    }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in names)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        throw new FileNotFoundException("No Arcana engine found. Run tools/fetch-engine.ps1, set ARCANA_ENGINE, or supply --executable=PATH.");
    }
}
