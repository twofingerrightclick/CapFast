namespace CapFast.Core;

public enum AppAppearance { Dark, Light }

public static class AppearancePreferences
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapFast", "appearance.txt");

    public static AppAppearance Load(string? path = null)
    {
        try { return File.ReadAllText(path ?? DefaultPath).Trim() == "Light" ? AppAppearance.Light : AppAppearance.Dark; }
        catch (IOException) { return AppAppearance.Dark; }
        catch (UnauthorizedAccessException) { return AppAppearance.Dark; }
    }

    public static void Save(AppAppearance appearance, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, appearance == AppAppearance.Light ? "Light" : "Dark");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
