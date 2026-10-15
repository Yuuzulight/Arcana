using System.Diagnostics;

namespace Arcana.Runtime;

public static class ArcanaFallback
{
    public static Process OpenDefaultBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("Fallback only accepts http or https URLs.", nameof(url));

        return Process.Start(new ProcessStartInfo
        {
            FileName = parsed.AbsoluteUri,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows could not open the default browser.");
    }
}
