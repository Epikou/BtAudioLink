using System.Windows;

namespace BtAudioLink;

static class Program
{
    /// <summary>Suffixe de profil (tests) : permet de lancer deux instances séparées sur le même PC.</summary>
    public static readonly string Profile = Environment.GetEnvironmentVariable("BTAUDIOLINK_PROFILE") ?? "";

    [STAThread]
    static int Main(string[] args)
    {
        bool minimized = args.Contains("--minimized");
        bool updated = args.Contains("--updated");

        // Une seule instance : une deuxième demande à la première d'afficher sa fenêtre, puis s'arrête.
        using var mutex = new Mutex(false, "BtAudioLink.Instance" + Profile);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "BtAudioLink.Show" + Profile);
        bool owned;
        try { owned = mutex.WaitOne(updated ? 15000 : 0); }       // après une mise à jour, on attend la fin de l'ancienne
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            showEvent.Set();
            return 0;
        }

        try
        {
            Updater.CleanUp();
            Settings.RefreshAutostartPath();

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Write($"Erreur d'interface : {e.Exception}");
                e.Handled = true;
            };

            var window = new MainWindow(Settings.Load());

            var listener = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    window.Dispatcher.BeginInvoke(window.ShowFromTray);
            }) { IsBackground = true, Name = "instance" };
            listener.Start();

            if (!minimized) window.ShowFromTray();
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Arrêt sur erreur : {ex}");
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (Exception) { }
        }
    }
}
