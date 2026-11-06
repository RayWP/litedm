namespace LiteDM.App;

internal static class StartupLog
{
    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteDM", "startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
