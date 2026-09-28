using System.Security.Cryptography;

namespace Baton.Host.Browser;

/// <summary>
/// The secret a Firefox build of the Baton extension presents (see <see cref="BrowserBridge.TokenPath"/>).
/// One per PC, kept next to the trust store; scripts\Build.ps1 bakes it into the .xpi.
/// </summary>
public static class BrowserToken
{
    public const string FileName = "browser-token";

    public static string EnsureFile(string storageDirectory)
    {
        var path = Path.Combine(storageDirectory, FileName);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(storageDirectory);
            File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        }

        return path;
    }
}
