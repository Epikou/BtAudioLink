using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace BtAudioLink;

/// <summary>Réglages de l'utilisateur, enregistrés dans %AppData%\BtAudioLink\settings.json.</summary>
public sealed class Settings
{
    public Role Role { get; set; } = Role.Receiver;
    public string? TargetAddress { get; set; }
    public string? TargetName { get; set; }
    public bool MuteLocal { get; set; } = true;
    /// <summary>true = profil « stable » (plus de marge, moins de coupures), false = latence minimale.</summary>
    public bool Stable { get; set; }
    public int Volume { get; set; } = 100;
    public string Language { get; set; } = "en";
    /// <summary>La liaison tournait à la dernière fermeture : on la relance au démarrage.</summary>
    public bool Running { get; set; }

    public int MarginMs => Stable ? 90 : 40;

    static readonly string FilePath = Path.Combine(Log.Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write($"Réglages illisibles : {ex.Message}"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write($"Réglages non enregistrés : {ex.Message}"); }
    }

    // ------------------------------------------------------------------ lancement au démarrage de Windows

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "BtAudioLink";

    public static bool Autostart
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunName) is string;
            }
            catch (Exception) { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --minimized");
                else key.DeleteValue(RunName, false);
            }
            catch (Exception ex) { Log.Write($"Démarrage automatique non modifié : {ex.Message}"); }
        }
    }

    /// <summary>Si l'exécutable a été déplacé, on remet le bon chemin dans l'entrée de démarrage.</summary>
    public static void RefreshAutostartPath()
    {
        if (Autostart) Autostart = true;
    }
}
