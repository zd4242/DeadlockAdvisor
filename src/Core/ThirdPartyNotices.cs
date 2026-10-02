using System.IO;

namespace DeadlockAdvisor.Core;

/// <summary>THIRD-PARTY-NOTICES.txt, built into the app: the licenses of everything it ships with.</summary>
public static class ThirdPartyNotices
{
    public const string FileName = "THIRD-PARTY-NOTICES.txt";

    public static byte[] Bytes()
    {
        using var resource = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(FileName)
                             ?? throw new InvalidOperationException($"{FileName} isn't built into the app.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }
}
