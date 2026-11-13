namespace Arcana.Runtime;

public static class EngineLocator
{
    public const string Version = "main-bd95294+arcana1";

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

    public static string? ResolveFolio(string? executable = null)
    {
        executable ??= Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") ?? Environment.GetEnvironmentVariable("FOLIO_ENGINE");
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var fullPath = Path.GetFullPath(executable);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "Folio.ContentHost.exe", "folio-content-host.exe" } : new[] { "Folio.ContentHost", "folio-content-host" };
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                foreach (var subdirectory in new[] { "engines/folio/current", "engines/folio", "engines", "" })
                    foreach (var name in names)
                    {
                        var path = Path.Combine(directory.FullName, subdirectory, name);
                        if (File.Exists(path)) return Path.GetFullPath(path);
                    }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in names)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }

        // Check common development feed paths
        var feedCandidates = new[]
        {
            @"C:\ManaAI\Folio\tools\Folio.ContentHost\bin\Release\net10.0\win-x64\publish\Folio.ContentHost.exe",
            @"C:\ManaAI\Folio\tools\Folio.ContentHost\bin\Release\net10.0\Folio.ContentHost.exe",
            @"D:\Mana\windows-native-launcher\folio-feed\content-host\8112827b09c517026a595c007b6bd2e3ff29e46a\win-x64\Folio.ContentHost.exe"
        };
        foreach (var candidate in feedCandidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }
}
