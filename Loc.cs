namespace BtAudioLink;

/// <summary>Textes de l'interface : anglais par défaut, français et espagnol en traduction.</summary>
public static class Loc
{
    public static string Language = "en";

    public static readonly (string Code, string Label)[] Languages = { ("en", "English"), ("fr", "Français"), ("es", "Español") };

    public static string T(string key)
    {
        if (Texts.TryGetValue(key, out var t))
            return Language switch { "fr" => t.Fr, "es" => t.Es, _ => t.En };
        return key;
    }

    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    static readonly Dictionary<string, (string En, string Fr, string Es)> Texts = new()
    {
        ["Sender"] = ("Sender", "Émetteur", "Emisor"),
        ["Receiver"] = ("Receiver", "Récepteur", "Receptor"),
        ["Start"] = ("Start", "Démarrer", "Iniciar"),
        ["Stop"] = ("Stop", "Arrêter", "Detener"),
        ["Autostart"] = ("Start with Windows", "Lancer au démarrage", "Iniciar con Windows"),
        ["Settings"] = ("Settings", "Réglages", "Ajustes"),
        ["Back"] = ("Back", "Retour", "Volver"),
        ["Close"] = ("Hide", "Réduire", "Ocultar"),

        ["StoppedTitle"] = ("Stopped", "Arrêté", "Detenido"),
        ["StoppedSender"] = ("Ready to send this PC's sound", "Prêt à envoyer le son de ce PC", "Listo para enviar el sonido de este PC"),
        ["StoppedReceiver"] = ("Ready to play another PC's sound", "Prêt à jouer le son d'un autre PC", "Listo para reproducir el sonido de otro PC"),
        ["WaitingTitle"] = ("Waiting", "En attente", "En espera"),
        ["WaitingSender"] = ("Looking for {0}", "Recherche de {0}", "Buscando {0}"),
        ["WaitingReceiver"] = ("Waiting for the sender", "En attente de l'émetteur", "Esperando al emisor"),
        ["SendingTitle"] = ("Sending sound", "Son envoyé", "Enviando sonido"),
        ["SendingSub"] = ("to {0}", "vers {0}", "a {0}"),
        ["ReceivingTitle"] = ("Receiving sound", "Son reçu", "Recibiendo sonido"),
        ["ReceivingSub"] = ("from {0} · buffer {1} ms", "de {0} · tampon {1} ms", "de {0} · búfer {1} ms"),
        ["ErrorTitle"] = ("Problem", "Problème", "Problema"),
        ["ErrBluetooth"] = ("Bluetooth is off or unavailable", "Bluetooth éteint ou indisponible", "Bluetooth apagado o no disponible"),
        ["ErrNoTarget"] = ("Choose a device in Settings", "Choisis un appareil dans Réglages", "Elige un dispositivo en Ajustes"),
        ["ErrGeneric"] = ("Something went wrong, see the log", "Erreur inattendue, voir le journal", "Error inesperado, consulta el registro"),

        ["SendTo"] = ("Send to", "Envoyer vers", "Enviar a"),
        ["NoPaired"] = ("No paired device. Pair the other PC in Windows Bluetooth settings first.",
                        "Aucun appareil appairé. Appaire d'abord l'autre PC dans les paramètres Bluetooth de Windows.",
                        "Ningún dispositivo emparejado. Empareja primero el otro PC en los ajustes de Bluetooth de Windows."),
        ["Refresh"] = ("Refresh", "Actualiser", "Actualizar"),
        ["MuteLocal"] = ("Mute this PC while sending", "Couper le son de ce PC pendant l'envoi", "Silenciar este PC al enviar"),
        ["Volume"] = ("Playback volume", "Volume de lecture", "Volumen de reproducción"),
        ["Profile"] = ("Connection", "Liaison", "Conexión"),
        ["LowLatency"] = ("Low latency", "Latence minimale", "Baja latencia"),
        ["Stable"] = ("Stable", "Stable", "Estable"),
        ["Language"] = ("Language", "Langue", "Idioma"),
        ["Version"] = ("Version {0}", "Version {0}", "Versión {0}"),

        ["CheckUpdate"] = ("Check for updates", "Chercher une mise à jour", "Buscar actualizaciones"),
        ["ForceUpdate"] = ("Force reinstall", "Forcer la réinstallation", "Forzar reinstalación"),
        ["UpdChecking"] = ("Checking…", "Recherche…", "Buscando…"),
        ["UpdUpToDate"] = ("You're up to date", "Tu es à jour", "Estás al día"),
        ["UpdAvailable"] = ("Version {0} is available", "La version {0} est disponible", "La versión {0} está disponible"),
        ["UpdInstall"] = ("Install", "Installer", "Instalar"),
        ["UpdDownloading"] = ("Downloading… {0} %", "Téléchargement… {0} %", "Descargando… {0} %"),
        ["UpdFailed"] = ("Update failed: {0}", "Mise à jour impossible : {0}", "Actualización fallida: {0}"),
        ["UpdNoRelease"] = ("No published version found", "Aucune version publiée", "No hay ninguna versión publicada"),

        ["TrayOpen"] = ("Open", "Ouvrir", "Abrir"),
        ["TrayQuit"] = ("Quit", "Quitter", "Salir"),
        ["TraySender"] = ("Role: sender", "Rôle : émetteur", "Rol: emisor"),
        ["TrayReceiver"] = ("Role: receiver", "Rôle : récepteur", "Rol: receptor"),
        ["TrayHint"] = ("BtAudioLink keeps running here", "BtAudioLink continue de tourner ici", "BtAudioLink sigue funcionando aquí"),
    };
}
