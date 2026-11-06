using System.Runtime.InteropServices;

namespace LiteDM.App;

internal static class WindowsFolderPaths
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string GetDownloads()
    {
        var id = DownloadsFolderId;
        if (SHGetKnownFolderPath(ref id, 0, 0, out var path) == 0)
        {
            try { return Marshal.PtrToStringUni(path) ?? Fallback(); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Fallback();
    }

    private static string Fallback() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, nint token, out nint path);
}
