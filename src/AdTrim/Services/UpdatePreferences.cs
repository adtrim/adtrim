using System.IO;

namespace AdTrim.Services;

public static class UpdatePreferences
{
    // Shared with the installer so its choice applies before the first launch.
    public static bool Load(string directory)
    {
        var path = Path.Combine(directory, "automatic-updates.txt");
        try { return File.ReadAllText(path).Trim() == "1"; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void Save(string directory, bool enabled)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "automatic-updates.txt");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, enabled ? "1" : "0");
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
