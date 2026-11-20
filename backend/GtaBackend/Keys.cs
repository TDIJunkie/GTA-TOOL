using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>GTA-V-Schlüssel (nötig für verschlüsselte RPF-Archive).</summary>
public static class Keys
{
    public static bool Loaded { get; private set; }
    public static string LastError { get; private set; }

    public static object Load(string gtaPath, string aesKey)
    {
        Loaded = false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(gtaPath) && string.IsNullOrWhiteSpace(aesKey))
        {
            LastError = "Kein GTA-V-Ordner eingestellt.";
            return Status();
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(aesKey))
            {
                GTA5Keys.LoadFromPath(gtaPath ?? ".", false, aesKey);
            }
            else
            {
                if (!File.Exists(Path.Combine(gtaPath, "GTA5.exe")))
                    throw new UserError("Im gewählten Ordner liegt keine GTA5.exe (bitte den GTA-V-Legacy-Ordner wählen).");
                Program.Progress("GTA-Schlüssel werden aus GTA5.exe gelesen …");
                GTA5Keys.LoadFromPath(gtaPath, false, null);
            }
            Loaded = GTA5Keys.PC_AES_KEY != null && GTA5Keys.PC_NG_KEYS != null;
            if (!Loaded) LastError = "Schlüssel konnten nicht gelesen werden.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        return Status();
    }

    public static object Status() => new
    {
        loaded = Loaded,
        error = LastError,
        aesKey = Loaded && GTA5Keys.PC_AES_KEY != null ? Convert.ToBase64String(GTA5Keys.PC_AES_KEY) : null
    };
}
