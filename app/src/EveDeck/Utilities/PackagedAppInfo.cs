using System.Runtime.InteropServices;
using System.Text;

namespace EveDeck.Utilities;

// Am I running from an MSIX package (the Microsoft Store build) or as a plain exe (the portable /
// Inno installer downloads)?
//
// EveDeck ships the SAME binary through both. Rather than compiling a separate Store variant --
// which would be a second build to keep in sync, and would silently drift the moment someone forgot
// a #if -- the few behaviours MSIX forbids are gated on this check at runtime.
//
// What MSIX changes, and why each one has to be gated:
//   * Self-update. A packaged app cannot modify its own install directory; the Store owns updates.
//     Velopack must not run at all.
//   * Registry writes for the evedeck:// protocol handler and for run-at-login. Inside the package
//     these are VIRTUALIZED -- the write appears to succeed, lands in the package's private hive,
//     and the OS never sees it. Both are declared in AppxManifest.xml instead, so doing them here
//     would be worse than useless: silently broken rather than obviously broken.
//   * Writing into another application's %APPDATA% (installing the companion Mumble plugin). Same
//     redirection, so Mumble would never find the plugin.
//
// Detection is GetCurrentPackageFullName, the documented way to ask this. It returns
// APPMODEL_ERROR_NO_PACKAGE (15700) when the process has no package identity.
internal static class PackagedAppInfo
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    private static bool? _isPackaged;

    // Cached: package identity cannot change during a process's lifetime, and this is read from UI
    // paths that run often.
    public static bool IsPackaged => _isPackaged ??= DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            // A packaged process reports "buffer too small" (it has a name to give us); an
            // unpackaged one reports APPMODEL_ERROR_NO_PACKAGE.
            if (result == AppModelErrorNoPackage) return false;
            if (result == ErrorInsufficientBuffer) return true;

            // Any other return is unexpected. Treat it as unpackaged: that keeps the plain-exe
            // downloads (the overwhelming majority of installs) behaving exactly as they always
            // have, and the worst case for a packaged build is a self-update attempt that fails
            // visibly rather than a portable build silently losing its updater.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // kernel32!GetCurrentPackageFullName is Windows 8+. EveDeck targets far newer than that,
            // so this should be unreachable -- but an unresolvable P/Invoke must not take the app
            // down at startup.
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
