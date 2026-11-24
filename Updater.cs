using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BtAudioLink;

/// <summary>
/// Mise à jour depuis les versions publiées sur GitHub : on lit la dernière « release », on télécharge son
/// exécutable, on remplace le nôtre et on redémarre.
/// </summary>
public static class Updater
{
    /// <summary>Dépôt GitHub qui publie les versions (propriétaire/nom).</summary>
    public const string Repository = "Epikou/BtAudioLink";
    const string AssetName = "BtAudioLink.exe";

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public sealed record Release(Version Version, string Tag, string DownloadUrl, long Size);

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BtAudioLink-updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Dernière version publiée, ou null s'il n'y en a pas (ou pas d'exécutable joint).</summary>
    public static async Task<Release?> GetLatestAsync()
    {
        using var http = NewClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                return new Release(version, tag, asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
        }
        return null;
    }

    public static bool IsNewer(Release release) =>
        new Version(release.Version.Major, release.Version.Minor, Math.Max(0, release.Version.Build)) >
        new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));

    /// <summary>Télécharge la version à côté de l'exécutable actuel. Renvoie le chemin du fichier téléchargé.</summary>
    public static async Task<string> DownloadAsync(Release release, Action<int> progress)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("exe path");
        string target = exe + ".new";

        using var http = NewClient();
        http.Timeout = TimeSpan.FromMinutes(15);
        using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? release.Size;
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long done = 0;
            int last = -1, n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != last) { last = percent; progress(percent); }
            }
        }

        // Garde-fou : un téléchargement tronqué ou une page d'erreur ne doit jamais remplacer l'exécutable.
        long size = new FileInfo(target).Length;
        if (release.Size > 0 && size != release.Size)
        {
            File.Delete(target);
            throw new IOException($"size {size} != {release.Size}");
        }
        return target;
    }

    /// <summary>Remplace l'exécutable par le fichier téléchargé et relance l'application.</summary>
    public static void ApplyAndRestart(string downloaded, bool minimized)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("exe path");
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);                 // Windows autorise le renommage d'un exécutable en cours d'utilisation
        try { File.Move(downloaded, exe); }
        catch (Exception)
        {
            File.Move(old, exe);             // on remet l'ancien en place si le remplacement échoue
            throw;
        }
        Process.Start(new ProcessStartInfo(exe, "--updated" + (minimized ? " --minimized" : "")) { UseShellExecute = false });
    }

    /// <summary>Supprime les restes d'une mise à jour précédente.</summary>
    public static void CleanUp()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        Task.Run(async () =>
        {
            await Task.Delay(3000);          // laisse l'ancien processus se terminer
            foreach (var path in new[] { exe + ".old", exe + ".new" })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
            }
        });
    }
}
