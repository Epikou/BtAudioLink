// Moteur audio : transporte le son d'un PC Windows vers un autre par la liaison série Bluetooth (RFCOMM).
// Windows ne sait pas recevoir le Bluetooth audio standard (A2DP) d'un autre PC Windows ; on transporte donc
// le son nous-mêmes : capture du son système -> Opus -> RFCOMM -> décodage -> lecture.

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Concentus.Enums;
using Concentus.Structs;
using InTheHand.Net;
using InTheHand.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BtAudioLink;

public enum Role { Sender, Receiver }

public enum LinkState { Stopped, Waiting, Connected, Error }

/// <summary>Photo de l'état du moteur, envoyée à l'interface.</summary>
public sealed record EngineStatus(LinkState State, Role Role, string? Peer, string? ErrorKey, int BufferMs, int Holes);

public sealed record PairedDevice(string Name, string Address);

public sealed class EngineOptions
{
    public Role Role;
    public string? TargetAddress;      // émetteur : appareil vers lequel envoyer
    public bool MuteLocal;             // émetteur : couper les haut-parleurs locaux pendant l'envoi
    public bool FollowVolume = true;   // émetteur : le volume de Windows règle le son joué sur l'autre PC
    public int MarginMs = 40;          // récepteur : marge de sécurité du tampon
    public float Volume = 1f;          // récepteur : volume de lecture
}

public sealed class Engine
{
    // Identifiant du service série Bluetooth, commun à l'émetteur et au récepteur.
    static readonly Guid ServiceId = new("7b1f3c52-9d4e-4a6b-8f21-5e0c9a3d4b10");

    const int Rate = 48000;
    const int Channels = 2;
    const int FrameSamples = 480;          // trames de 10 ms
    const int BitrateBps = 128_000;
    const int MaxPayload = 4000;

    // Messages : [type : 1 octet][longueur : 2 octets][contenu].
    const byte MsgHello = 1, MsgAudio = 2, MsgPing = 4, MsgStats = 5, MsgVolume = 6;
    const byte ProtocolVersion = 2;

    public event Action<EngineStatus>? Changed;

    readonly object gate = new();
    CancellationTokenSource? cts;
    Thread? worker;
    IDisposable? activeTransport;          // listener ou client en cours, fermé par Stop pour débloquer le fil
    EngineOptions options = new();
    volatile JitterBuffer? liveBuffer;
    volatile float remoteGain = 1f;     // volume demandé par l'émetteur (1 = plein volume)

    public bool Running { get { lock (gate) return cts != null; } }

    /// <summary>Liste les appareils Bluetooth appairés (pour choisir vers qui envoyer).</summary>
    public static List<PairedDevice> ListPaired()
    {
        try
        {
            using var client = new BluetoothClient();
            return client.PairedDevices
                .Select(d => new PairedDevice(string.IsNullOrWhiteSpace(d.DeviceName) ? d.DeviceAddress.ToString() : d.DeviceName, d.DeviceAddress.ToString()))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"Liste des appareils impossible : {ex.Message}");
            return new List<PairedDevice>();
        }
    }

    public void Start(EngineOptions opts)
    {
        Stop();
        lock (gate)
        {
            options = opts;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            worker = new Thread(() => Run(opts, token)) { IsBackground = true, Name = "moteur" };
            worker.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (gate)
        {
            if (cts == null) return;
            cts.Cancel();
            try { activeTransport?.Dispose(); } catch (Exception) { }
            activeTransport = null;
            t = worker;
            worker = null;
            cts = null;
        }
        t?.Join(3000);
        Publish(new EngineStatus(LinkState.Stopped, options.Role, null, null, 0, 0));
    }

    /// <summary>Réglages modifiables pendant que la liaison tourne.</summary>
    public void SetLive(int marginMs, float volume)
    {
        options.MarginMs = marginMs;
        options.Volume = volume;
        var b = liveBuffer;
        if (b != null) { b.SetMargin(marginMs); b.Gain = volume * remoteGain; }
    }

    void Publish(EngineStatus s)
    {
        try { Changed?.Invoke(s); } catch (Exception) { }
    }

    void Track(IDisposable? transport)
    {
        lock (gate) activeTransport = transport;
    }

    // ------------------------------------------------------------------ boucle principale

    void Run(EngineOptions opts, CancellationToken token)
    {
        string? tcp = Environment.GetEnvironmentVariable("BTAUDIOLINK_TCP");      // mode test local, sans Bluetooth
        try
        {
            if (opts.Role == Role.Receiver) RunReceiver(opts, token, tcp);
            else RunSender(opts, token, tcp);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Log.Write($"Moteur arrêté sur erreur : {ex}");
            Publish(new EngineStatus(LinkState.Error, opts.Role, null, "ErrGeneric", 0, 0));
        }
        catch (Exception) { }
    }

    void RunReceiver(EngineOptions opts, CancellationToken token, string? tcp)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (tcp != null)
                {
                    var listener = new TcpListener(IPAddress.Loopback, int.Parse(tcp));
                    listener.Start();
                    Track(new Disposer(listener.Stop));
                    Publish(new EngineStatus(LinkState.Waiting, opts.Role, null, null, 0, 0));
                    while (!token.IsCancellationRequested)
                    {
                        using var c = listener.AcceptTcpClient();
                        c.NoDelay = true;
                        Session(c.GetStream(), opts, token, "TCP");
                        Publish(new EngineStatus(LinkState.Waiting, opts.Role, null, null, 0, 0));
                    }
                    return;
                }

                var bt = new BluetoothListener(ServiceId) { ServiceName = "BtAudioLink" };
                bt.Start();
                Track(new Disposer(bt.Stop));
                Publish(new EngineStatus(LinkState.Waiting, opts.Role, null, null, 0, 0));
                Log.Write("Récepteur en attente.");
                while (!token.IsCancellationRequested)
                {
                    using var c = bt.AcceptBluetoothClient();
                    string name;
                    try { name = c.RemoteMachineName; } catch { name = "?"; }
                    Session(c.GetStream(), opts, token, name);
                    Publish(new EngineStatus(LinkState.Waiting, opts.Role, null, null, 0, 0));
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // Bluetooth éteint ou indisponible : on le signale et on réessaie.
                Log.Write($"Écoute impossible : {ex.Message}");
                Publish(new EngineStatus(LinkState.Error, opts.Role, null, "ErrBluetooth", 0, 0));
                token.WaitHandle.WaitOne(3000);
            }
        }
    }

    void RunSender(EngineOptions opts, CancellationToken token, string? tcp)
    {
        if (tcp == null && string.IsNullOrEmpty(opts.TargetAddress))
        {
            Publish(new EngineStatus(LinkState.Error, opts.Role, null, "ErrNoTarget", 0, 0));
            return;
        }

        string? peerName = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (tcp != null)
                {
                    using var c = new TcpClient("127.0.0.1", int.Parse(tcp)) { NoDelay = true };
                    Track(c);
                    Session(c.GetStream(), opts, token, "TCP");
                }
                else
                {
                    using var c = new BluetoothClient();
                    Track(c);
                    var address = BluetoothAddress.Parse(opts.TargetAddress!);
                    peerName ??= c.PairedDevices.FirstOrDefault(d => d.DeviceAddress == address)?.DeviceName;
                    Publish(new EngineStatus(LinkState.Waiting, opts.Role, peerName, null, 0, 0));
                    c.Connect(address, ServiceId);
                    Session(c.GetStream(), opts, token, peerName ?? opts.TargetAddress!);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // Le récepteur n'est pas lancé, hors de portée, ou le Bluetooth est éteint : on réessaie sans fin.
                Log.Write($"Pas de liaison : {ex.Message}");
                Publish(new EngineStatus(LinkState.Waiting, opts.Role, peerName, "WaitPeer", 0, 0));
            }
            token.WaitHandle.WaitOne(2000);
        }
    }

    sealed class Disposer(Action action) : IDisposable
    {
        public void Dispose() { try { action(); } catch (Exception) { } }
    }

    // ------------------------------------------------------------------ session

    void Session(Stream stream, EngineOptions opts, CancellationToken token, string peer)
    {
        Log.Write($"Connecté à {peer}.");
        try { stream.WriteTimeout = 4000; stream.ReadTimeout = 6000; } catch (Exception) { }
        var writeLock = new object();
        using var done = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, done.Token);

        void Send(byte type, byte[] payload, int length)
        {
            var frame = new byte[length + 3];
            frame[0] = type;
            frame[1] = (byte)length;
            frame[2] = (byte)(length >> 8);
            Buffer.BlockCopy(payload, 0, frame, 3, length);
            // Flush indispensable : certains flux retiennent les écritures tant que leur tampon n'est pas plein.
            lock (writeLock) { stream.Write(frame, 0, frame.Length); stream.Flush(); }
        }

        try
        {
            var hello = new List<byte> { ProtocolVersion };
            hello.AddRange(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName));
            Send(MsgHello, hello.ToArray(), hello.Count);

            // Battement régulier : sans lui, on ne saurait pas distinguer un silence d'une liaison coupée.
            var pinger = new Thread(() =>
            {
                try
                {
                    while (!linked.Token.WaitHandle.WaitOne(1000)) Send(MsgPing, Array.Empty<byte>(), 0);
                }
                catch (Exception) { done.Cancel(); }
            }) { IsBackground = true, Name = "battement" };
            pinger.Start();

            using var priority = Realtime.AudioThread();
            if (opts.Role == Role.Sender) SenderSession(stream, Send, opts, linked.Token, peer, done);
            else ReceiverSession(stream, opts, linked.Token, peer);
        }
        catch (Exception ex)
        {
            Log.Write($"Liaison terminée ({ex.GetType().Name} : {ex.Message}).");
        }
        finally
        {
            done.Cancel();
            liveBuffer = null;
        }
    }

    static (byte type, int length) ReadFrame(Stream stream, byte[] payload)
    {
        Span<byte> header = stackalloc byte[3];
        stream.ReadExactly(header);
        int length = header[1] | (header[2] << 8);
        if (length > MaxPayload) throw new IOException("trame invalide");
        if (length > 0) stream.ReadExactly(payload, 0, length);
        return (header[0], length);
    }

    // ------------------------------------------------------------------ réception

    void ReceiverSession(Stream stream, EngineOptions opts, CancellationToken token, string peer)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
        var decoder = new OpusDecoder(Rate, Channels);
        var pcm = new float[5760 * Channels];
        var payload = new byte[MaxPayload];
        var buffer = new JitterBuffer(format, opts.MarginMs) { Gain = opts.Volume };
        remoteGain = 1f;
        liveBuffer = buffer;
        using var output = new Player(buffer);
        var stats = Stopwatch.StartNew();
        string name = peer;
        var buffered = new BufferedStream(stream, 64 * 1024);

        // Mesures pour le journal (toutes les 5 s) : elles servent à régler la liaison sans tâtonner.
        var clock = Stopwatch.StartNew();
        var wait = new Stopwatch();
        var logTimer = Stopwatch.StartNew();
        long totalSamples = 0, receivedSamples = 0, maxWait = 0, maxBusy = 0;
        double lateMin = double.MaxValue, lateMax = double.MinValue;

        Publish(new EngineStatus(LinkState.Connected, Role.Receiver, name, null, 0, 0));
        while (!token.IsCancellationRequested)
        {
            long busy = wait.ElapsedMilliseconds;                 // temps passé hors lecture depuis la trame précédente
            if (busy > maxBusy && wait.IsRunning) maxBusy = busy;
            wait.Restart();
            var (type, length) = ReadFrame(buffered, payload);
            long waited = wait.ElapsedMilliseconds;
            wait.Restart();
            switch (type)
            {
                case MsgHello:
                    if (length > 1) name = System.Text.Encoding.UTF8.GetString(payload, 1, length - 1);
                    break;
                case MsgVolume:
                    if (length >= 2)
                    {
                        // Courbe quadratique : le curseur de Windows est perceptif, un gain linéaire paraîtrait
                        // presque sans effet sur la moitié haute de la course.
                        float level = Math.Clamp(payload[0], (byte)0, (byte)100) / 100f;
                        remoteGain = payload[1] != 0 ? 0f : level * level;
                        buffer.Gain = opts.Volume * remoteGain;
                        Log.Write($"  volume distant : {payload[0]} %{(payload[1] != 0 ? ", muet" : "")}");
                    }
                    break;
                case MsgStats:
                    Log.Write("  émetteur : " + System.Text.Encoding.UTF8.GetString(payload, 0, length));
                    break;
                case MsgAudio:
                    if (waited > maxWait) maxWait = waited;
                    int samples = decoder.Decode(payload, 0, length, pcm, 0, 5760, false);
                    // Retard de la trame par rapport à un flux parfaitement régulier : l'écart entre le plus
                    // grand et le plus petit retard est la gigue que le tampon doit absorber.
                    double late = clock.Elapsed.TotalMilliseconds - totalSamples * 1000.0 / Rate;
                    totalSamples += samples;
                    receivedSamples += samples;
                    if (late < lateMin) lateMin = late;
                    if (late > lateMax) lateMax = late;
                    buffer.Add(pcm, samples * Channels);
                    break;
            }

            if (stats.ElapsedMilliseconds >= 1000)
            {
                var (advance, holes) = buffer.TakeStats();
                Publish(new EngineStatus(LinkState.Connected, Role.Receiver, name, null, advance, holes));
                stats.Restart();
            }

            if (logTimer.ElapsedMilliseconds >= 5000)
            {
                double jitter = lateMax >= lateMin ? lateMax - lateMin : 0;
                Log.Write($"  mesures : {receivedSamples / (double)Rate:0.0} s de son en {logTimer.Elapsed.TotalSeconds:0.0} s, gigue {jitter:0} ms, attente max {maxWait} ms, traitement max {maxBusy} ms, {buffer.Describe()}");
                receivedSamples = maxWait = maxBusy = 0;
                lateMin = double.MaxValue;
                lateMax = double.MinValue;
                logTimer.Restart();
            }
        }
    }

    // ------------------------------------------------------------------ émission

    void SenderSession(Stream stream, Action<byte, byte[], int> send, EngineOptions opts, CancellationToken token, string peer, CancellationTokenSource done)
    {
        string name = peer;

        // Lecture des messages du récepteur ; sert aussi à détecter tout de suite une liaison coupée.
        var reader = new Thread(() =>
        {
            var payload = new byte[MaxPayload];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var (type, length) = ReadFrame(stream, payload);
                    if (type == MsgHello && length > 1)
                    {
                        name = System.Text.Encoding.UTF8.GetString(payload, 1, length - 1);
                        Publish(new EngineStatus(LinkState.Connected, Role.Sender, name, null, 0, 0));
                    }
                }
            }
            catch (Exception) { }
            done.Cancel();
        }) { IsBackground = true, Name = "lecture" };
        reader.Start();

        using var capture = new FastLoopbackCapture();
        // Créé après la capture, libéré avant elle : la sourdine locale ne dure que le temps de l'envoi.
        using var volume = opts.MuteLocal || opts.FollowVolume
            ? new LocalVolume(opts.MuteLocal, opts.FollowVolume, (level, muted) => send(MsgVolume, new[] { (byte)level, (byte)(muted ? 1 : 0) }, 2))
            : null;
        var source = new BufferedWaveProvider(capture.WaveFormat)
        {
            // Tampon court : si le Bluetooth ne suit pas, on jette plutôt que d'accumuler du retard.
            BufferDuration = TimeSpan.FromMilliseconds(300),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        using var signal = new AutoResetEvent(false);
        long capturedFrames = 0;
        int blockAlign = capture.WaveFormat.BlockAlign;
        capture.DataAvailable += (_, e) =>
        {
            Interlocked.Add(ref capturedFrames, e.BytesRecorded / blockAlign);
            source.AddSamples(e.Buffer, 0, e.BytesRecorded);
            try { signal.Set(); } catch (ObjectDisposedException) { }
        };
        capture.RecordingStopped += (_, _) => done.Cancel();      // sortie audio changée : on rouvre la session

        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.Channels == 1)
            samples = new MonoToStereoSampleProvider(samples);
        else if (samples.WaveFormat.Channels > 2)
            samples = new MultiplexingSampleProvider(new[] { samples }, 2);   // garde avant gauche/droite
        if (samples.WaveFormat.SampleRate != Rate)
            samples = new WdlResamplingSampleProvider(samples, Rate);

        var encoder = new OpusEncoder(Rate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY)
        {
            Bitrate = BitrateBps,
            Complexity = 5,
            SignalType = OpusSignal.OPUS_SIGNAL_MUSIC,
        };

        var frame = new float[FrameSamples * Channels];
        var packet = new byte[MaxPayload];
        int filled = 0;
        long sentSamples = 0, maxWrite = 0;
        var writeTimer = new Stopwatch();
        var report = Stopwatch.StartNew();

        capture.StartRecording();
        Publish(new EngineStatus(LinkState.Connected, Role.Sender, name, null, 0, 0));
        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = samples.Read(frame, filled, frame.Length - filled);
                if (n == 0)
                {
                    WaitHandle.WaitAny(new[] { signal, token.WaitHandle }, 20);
                    continue;
                }
                filled += n;
                if (filled < frame.Length) continue;
                filled = 0;

                int len = encoder.Encode(frame, 0, FrameSamples, packet, 0, MaxPayload);
                writeTimer.Restart();
                send(MsgAudio, packet, len);
                if (writeTimer.ElapsedMilliseconds > maxWrite) maxWrite = writeTimer.ElapsedMilliseconds;
                sentSamples += FrameSamples;

                // Compte rendu au récepteur toutes les 5 s : c'est lui qui tient le journal de la liaison.
                if (report.ElapsedMilliseconds >= 5000)
                {
                    double captured = Interlocked.Exchange(ref capturedFrames, 0) / (double)capture.WaveFormat.SampleRate;
                    string text = $"capté {captured:0.0} s, envoyé {sentSamples / (double)Rate:0.0} s en {report.Elapsed.TotalSeconds:0.0} s, écriture max {maxWrite} ms";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                    send(MsgStats, bytes, bytes.Length);
                    sentSamples = maxWrite = 0;
                    report.Restart();
                }
            }
        }
        finally
        {
            try { capture.StopRecording(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Capture du son système pilotée par événements. La capture standard de NAudio relit le son toutes les
    /// 50 ms : il part alors par blocs de 50 ms, ce qui impose autant de latence en plus à l'arrivée.
    /// </summary>
    sealed class FastLoopbackCapture : WasapiCapture
    {
        public FastLoopbackCapture() : base(WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(), true, 20) { }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
    }

    /// <summary>
    /// Volume de la sortie audio locale, côté émetteur. Deux rôles :
    /// - couper les haut-parleurs locaux pendant l'envoi (la capture du son système se fait avant le volume du
    ///   périphérique, elle continue donc malgré la sourdine) ;
    /// - faire du volume de Windows la télécommande du son joué sur l'autre PC, comme avec des écouteurs
    ///   Bluetooth : le niveau et la sourdine sont envoyés au récepteur, qui les applique.
    /// Quand les deux sont actifs, la sortie doit rester en sourdine alors que Windows l'enlève dès qu'on touche
    /// au volume : on la remet aussitôt, et la touche « muet » devient une bascule du son distant.
    /// </summary>
    sealed class LocalVolume : IDisposable
    {
        readonly MMDevice? device;
        readonly bool wasMuted, enforceMute, follow;
        readonly Action<int, bool> report;
        readonly object gate = new();
        float lastVolume;
        bool remoteMute;
        long lastVolumeTick;
        int sentLevel = -1;
        bool sentMute;

        public LocalVolume(bool enforceMute, bool follow, Action<int, bool> report)
        {
            this.enforceMute = enforceMute;
            this.follow = follow;
            this.report = report;
            try
            {
                device = WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
                var volume = device.AudioEndpointVolume;
                wasMuted = volume.Mute;
                lastVolume = volume.MasterVolumeLevelScalar;
                remoteMute = !enforceMute && wasMuted;
                if (enforceMute) volume.Mute = true;
                if (follow)
                {
                    volume.OnVolumeNotification += OnNotification;
                    Report();
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Volume local inaccessible : {ex.Message}");
                device = null;
            }
        }

        void OnNotification(AudioVolumeNotificationData data)
        {
            bool remute = false;
            lock (gate)
            {
                long now = Environment.TickCount64;
                bool volumeChanged = Math.Abs(data.MasterVolume - lastVolume) > 0.0005f;
                if (volumeChanged)
                {
                    lastVolume = data.MasterVolume;
                    lastVolumeTick = now;
                }

                if (!enforceMute)
                {
                    remoteMute = data.Muted;
                }
                else
                {
                    // Toucher au volume rétablit toujours le son distant, comme le fait Windows pour le son local.
                    if (volumeChanged) remoteMute = false;
                    if (!data.Muted)
                    {
                        // Sourdine enlevée sans changement de volume récent : c'est la touche « muet ».
                        if (!volumeChanged && now - lastVolumeTick > 300) remoteMute = !remoteMute;
                        remute = true;
                    }
                }
                Report();
            }

            // Hors de la notification elle-même : modifier le volume depuis son propre rappel peut bloquer.
            if (remute)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { device!.AudioEndpointVolume.Mute = true; } catch (Exception) { }
                });
            }
        }

        void Report()
        {
            int level = (int)Math.Round(lastVolume * 100);
            if (level == sentLevel && remoteMute == sentMute) return;
            sentLevel = level;
            sentMute = remoteMute;
            try { report(level, remoteMute); } catch (Exception) { }
        }

        public void Dispose()
        {
            if (device == null) return;
            try
            {
                if (follow) device.AudioEndpointVolume.OnVolumeNotification -= OnNotification;
                if (enforceMute && !wasMuted) device.AudioEndpointVolume.Mute = false;
            }
            catch (Exception) { }
        }
    }
    /// <summary>
    /// Sortie audio qui suit le périphérique par défaut de Windows : si on allume/éteint un casque ou qu'on
    /// change de sortie, la lecture s'arrête côté Windows et il faut la rouvrir sur le nouveau périphérique.
    /// </summary>
    sealed class Player : IDisposable
    {
        readonly ISampleProvider source;
        readonly MMDeviceEnumerator enumerator = new();
        WasapiOut? output;
        string? deviceId;
        volatile bool stopped;
        long lastCheck = -10_000;

        readonly object sync = new();
        readonly Timer timer;
        bool disposed;

        public Player(ISampleProvider source)
        {
            this.source = source;
            Ensure();
            // La vérification du périphérique par défaut interroge Windows et peut prendre du temps : elle tourne
            // sur une minuterie à part, jamais sur le fil qui reçoit le son.
            timer = new Timer(_ => Ensure(), null, 1000, 1000);
        }

        void Ensure()
        {
            lock (sync)
            {
                if (disposed) return;
                EnsureLocked();
            }
        }

        void EnsureLocked()
        {
            long now = Environment.TickCount64;
            if (now - lastCheck < 900) return;
            lastCheck = now;

            MMDevice? device = null;
            try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia); }
            catch (Exception) { /* aucune sortie audio pour l'instant */ }

            if (output != null && !stopped && device?.ID == deviceId) return;

            output?.Dispose();
            output = null;
            stopped = false;
            if (device == null) return;

            try
            {
                var o = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
                o.PlaybackStopped += (_, e) =>
                {
                    stopped = true;
                    if (e.Exception != null) Log.Write($"Sortie audio interrompue ({e.Exception.Message}).");
                };
                o.Init(source);
                o.Play();
                output = o;
                deviceId = device.ID;
                Log.Write($"Sortie audio : {device.FriendlyName}");
            }
            catch (Exception ex)
            {
                Log.Write($"Impossible d'ouvrir la sortie audio ({ex.Message}).");
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            lock (sync)
            {
                disposed = true;
                output?.Dispose();
            }
        }
    }

    /// <summary>
    /// Tampon anti-gigue. Le Bluetooth livre le son par à-coups ; on garde donc une avance de son, la plus
    /// petite possible. Le tampon mesure en continu son niveau le plus bas (le « plancher ») : tout ce qui
    /// dépasse la marge de sécurité est de la latence inutile, retirée en douceur (un échantillon sur 48,
    /// inaudible). À l'inverse, chaque trou agrandit un peu la marge.
    /// </summary>
    sealed class JitterBuffer : ISampleProvider
    {
        readonly float[] ring = new float[Rate * Channels * 3];
        readonly object gate = new();
        const int ShortWindowMs = 2000;
        int margin, extra, read, count, toDrop, toAdd, dropPhase, shortMin = int.MaxValue;
        long shortStart, catchUpUntil;
        bool buffering = true;
        int windowMin = int.MaxValue;
        int underruns;
        long windowStart, lastUnderrunTick = Environment.TickCount64, pendingTick = -1;

        public JitterBuffer(WaveFormat format, int marginMs)
        {
            WaveFormat = format;
            margin = ToSamples(marginMs);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>Volume de lecture (1 = inchangé).</summary>
        public volatile float Gain = 1f;

        static int ToSamples(int ms) => Rate / 1000 * ms * Channels;
        static int ToMs(long samples) => (int)(samples / Channels * 1000 / Rate);

        // Fenêtre d'observation du plancher. Les à-coups du Bluetooth les plus forts reviennent toutes les
        // quelques dizaines de secondes : une fenêtre trop courte les oublie et resserre trop le tampon.
        const int WindowMs = 10_000;

        // Avance reconstituée avant de (re)jouer : la marge, plus de quoi couvrir les à-coups habituels (~60 ms).
        int Refill => margin + extra + ToSamples(60);

        public void SetMargin(int ms)
        {
            lock (gate) margin = ToSamples(ms);
        }

        public void Add(float[] pcm, int n)
        {
            float gain = Gain;
            if (gain != 1f)
                for (int i = 0; i < n; i++) pcm[i] *= gain;

            lock (gate)
            {
                if (pendingTick >= 0)
                {
                    // Le son revient vite après un manque : c'était un trou Bluetooth, on agrandit la marge.
                    // S'il revient tard, c'était une pause normale (Windows n'envoie rien pendant les silences).
                    if (Environment.TickCount64 - pendingTick < 1500)
                    {
                        underruns++;
                        lastUnderrunTick = Environment.TickCount64;
                        extra = Math.Min(extra + ToSamples(15), ToSamples(300));
                        catchUpUntil = Environment.TickCount64 + 3000;
                    }
                    pendingTick = -1;
                }

                // Après un blocage du lien, tout le son retenu arrive d'un coup. Le garder ajouterait des centaines
                // de millisecondes de retard pendant de longues secondes : on saute directement au plus récent.
                if (Environment.TickCount64 < catchUpUntil && count + n > Refill + ToSamples(60))
                {
                    int skip = count + n - Refill;
                    skip = Math.Min(skip - skip % Channels, count);
                    read = (read + skip) % ring.Length;
                    count -= skip;
                }

                // Garde-fou : énorme retard accumulé (longue rafale après un blocage), on saute au plus récent.
                if (count + n > Refill + ToSamples(500))
                {
                    int skip = count + n - Refill;
                    skip = Math.Min(skip - skip % Channels, count);
                    read = (read + skip) % ring.Length;
                    count -= skip;
                }

                int write = (read + count) % ring.Length;
                int first = Math.Min(n, ring.Length - write);
                Array.Copy(pcm, 0, ring, write, first);
                Array.Copy(pcm, first, ring, 0, n - first);
                count += n;
                if (buffering && count >= Refill)
                {
                    buffering = false;
                    windowStart = shortStart = Environment.TickCount64;
                    windowMin = shortMin = int.MaxValue;
                }
            }
        }

        public int Read(float[] buffer, int offset, int n)
        {
            lock (gate)
            {
                // Attention : NAudio passe ici un byte[] déguisé en float[] (WaveBuffer). Array.Copy et
                // Array.Clear refusent ce tableau ; il faut écrire élément par élément.
                if (buffering)
                {
                    for (int i = 0; i < n; i++) buffer[offset + i] = 0f;
                    return n;
                }

                bool starved = false;
                int o = offset, end = offset + n - 1;
                while (o < end)
                {
                    if (count >= 2)
                    {
                        float left = ring[read], right = ring[read + 1];
                        buffer[o++] = left;
                        buffer[o++] = right;
                        read += 2;
                        if (read >= ring.Length) read = 0;
                        count -= 2;

                        // Asservissement de l'avance, une paire d'échantillons sur 48 au plus (2 %, inaudible) :
                        // on en saute une pour réduire l'avance, ou on en répète une pour l'augmenter.
                        if (++dropPhase >= 48)
                        {
                            dropPhase = 0;
                            if (toDrop > 0 && count >= 2)
                            {
                                read += 2;
                                if (read >= ring.Length) read = 0;
                                count -= 2;
                                toDrop -= 2;
                            }
                            else if (toAdd > 0 && o < end)
                            {
                                buffer[o++] = left;
                                buffer[o++] = right;
                                toAdd -= 2;
                            }
                        }
                    }
                    else
                    {
                        starved = true;
                        buffer[o++] = 0f;
                        buffer[o++] = 0f;
                    }
                }
                if (o < offset + n) buffer[o] = 0f;

                long now = Environment.TickCount64;
                if (starved)
                {
                    // Plus rien à jouer : silence, puis on reconstitue une petite avance avant de reprendre.
                    // On ne sait pas encore si c'est un trou Bluetooth ou une vraie pause du son source.
                    buffering = true;
                    pendingTick = now;
                    toDrop = toAdd = 0;
                    logMin = 0;
                    return n;
                }

                if (count < windowMin) windowMin = count;
                if (count < shortMin) shortMin = count;
                if (count < logMin) logMin = count;

                // Fenêtre courte : le plancher est passé sous la marge (dérive d'horloge entre les deux PC, ou
                // à-coups plus forts). On regonfle l'avance tout de suite, avant que le tampon ne se vide.
                if (now - shortStart >= ShortWindowMs)
                {
                    int lack = margin + extra - shortMin;
                    if (lack > ToSamples(8))
                    {
                        toAdd = lack - lack % Channels;
                        toDrop = 0;
                        windowMin = int.MaxValue;
                        windowStart = now;
                    }
                    shortMin = int.MaxValue;
                    shortStart = now;
                }

                if (now - windowStart >= WindowMs)
                {
                    // Fenêtre longue : le plancher dépasse la marge, cet excédent ne sert à rien.
                    int excess = windowMin - (margin + extra);
                    toDrop = excess > ToSamples(5) && toAdd == 0 ? excess - excess % Channels : 0;
                    windowMin = int.MaxValue;
                    windowStart = now;

                    // Deux minutes sans trou : on rend un peu de la marge ajoutée.
                    if (extra > 0 && now - lastUnderrunTick > 120_000)
                    {
                        extra = Math.Max(0, extra - ToSamples(5));
                        lastUnderrunTick = now;
                    }
                }
                return n;
            }
        }

        /// <summary>Avance actuelle en ms et nombre de trous depuis le dernier appel.</summary>
        public (int advanceMs, int holes) TakeStats()
        {
            lock (gate)
            {
                int holes = underruns;
                underruns = 0;
                logHoles += holes;
                return (ToMs(count), holes);
            }
        }

        int logHoles, logMin = int.MaxValue;

        /// <summary>Résumé pour le journal : marge, avance, niveau le plus bas et trous depuis le dernier appel.</summary>
        public string Describe()
        {
            lock (gate)
            {
                string min = logMin == int.MaxValue ? "-" : ToMs(logMin).ToString();
                string s = $"marge {ToMs(margin + extra)} ms, avance {ToMs(count)} ms, plancher {min} ms, {logHoles} trous";
                logHoles = 0;
                logMin = int.MaxValue;
                return s;
            }
        }
    }
}

/// <summary>Journal sur disque, écrit sur un fil à part pour ne jamais retarder le son.</summary>
public static class Log
{
    static readonly System.Collections.Concurrent.BlockingCollection<string> Queue = new();
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BtAudioLink" + Program.Profile);
    static readonly string FilePath = Path.Combine(Folder, "log.txt");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
        }
        catch (Exception) { }

        new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine); }
                catch (Exception) { }
            }
        }) { IsBackground = true, Name = "journal" }.Start();
    }

    public static void Write(string message) => Queue.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
}
