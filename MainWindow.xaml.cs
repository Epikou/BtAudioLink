using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BtAudioLink;

public partial class MainWindow : Window
{
    readonly Settings settings;
    readonly Engine engine = new();
    readonly Tray tray;
    readonly Gamepad gamepad;
    EngineStatus status;
    Updater.Release? pendingUpdate;
    bool userMoved, updating, hintShown, reallyClosing;

    static readonly Brush Green = Frozen("#3CB464"), Orange = Frozen("#E6AA28"), Red = Frozen("#DC5050"), Gray = Frozen("#7D7D7D");

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public MainWindow(Settings settings)
    {
        InitializeComponent();
        this.settings = settings;
        Loc.Language = settings.Language;
        status = new EngineStatus(LinkState.Stopped, settings.Role, null, null, 0, 0);

        tray = new Tray(this);
        gamepad = new Gamepad(this);
        engine.Changed += s => Dispatcher.BeginInvoke(() => OnStatus(s));

        foreach (var (code, label) in Loc.Languages)
        {
            var chip = new RadioButton { Content = label, Tag = code, GroupName = "Language", Style = (Style)FindResource("Chip"), IsChecked = code == settings.Language };
            chip.Click += Language_Click;
            LanguageList.Children.Add(chip);
        }

        SenderRadio.IsChecked = settings.Role == Role.Sender;
        ReceiverRadio.IsChecked = settings.Role == Role.Receiver;
        AutostartSwitch.IsChecked = Settings.Autostart;
        MuteSwitch.IsChecked = settings.MuteLocal;
        VolumeSlider.Value = settings.Volume;
        LowLatencyRadio.IsChecked = !settings.Stable;
        StableRadio.IsChecked = settings.Stable;

        ApplyLanguage();
        SizeChanged += (_, _) => { if (!userMoved) PlaceNearTray(); };

        if (settings.Running) StartEngine();
        _ = CheckUpdateAsync(silent: true, force: false);
    }

    // ------------------------------------------------------------------ fenêtre

    /// <summary>Colle la fenêtre en bas à droite, au-dessus de la zone de notification.</summary>
    void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!userMoved) { UpdateLayout(); PlaceNearTray(); }
        Activate();
        StartButton.Focus();
    }

    public void ToggleVisible()
    {
        if (IsVisible && IsActive) HideToTray();
        else ShowFromTray();
    }

    public void HideToTray()
    {
        Hide();
        if (!hintShown) { hintShown = true; tray.ShowHint(); }
    }

    public void Quit()
    {
        reallyClosing = true;
        settings.Save();
        engine.Stop();
        tray.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // La croix et Alt+F4 réduisent près de l'heure ; « Quitter » est dans le menu de l'icône.
        if (!reallyClosing)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        userMoved = true;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        bool open = SettingsView.Visibility != Visibility.Visible;
        SettingsView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MainView.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = open ? Visibility.Hidden : Visibility.Visible;
        if (open)
        {
            UpdateRoleSections();
            LoadDevices();
            BackButton.Focus();
        }
        else StartButton.Focus();
    }

    // ------------------------------------------------------------------ langue et textes

    void ApplyLanguage()
    {
        SenderRadio.Content = Loc.T("Sender");
        ReceiverRadio.Content = Loc.T("Receiver");
        AutostartLabel.Text = Loc.T("Autostart");
        SettingsTitle.Text = Loc.T("Settings");
        SettingsButton.ToolTip = Loc.T("Settings");
        CloseButton.ToolTip = Loc.T("Close");
        BackButton.ToolTip = Loc.T("Back");
        RefreshButton.ToolTip = Loc.T("Refresh");
        SendToLabel.Text = Loc.T("SendTo");
        NoDeviceText.Text = Loc.T("NoPaired");
        MuteLabel.Text = Loc.T("MuteLocal");
        VolumeLabel.Text = Loc.T("Volume");
        ProfileLabel.Text = Loc.T("Profile");
        LowLatencyRadio.Content = Loc.T("LowLatency");
        StableRadio.Content = Loc.T("Stable");
        LanguageLabel.Text = Loc.T("Language");
        VersionText.Text = Loc.T("Version", Updater.CurrentText);
        CheckUpdateButton.Content = Loc.T("CheckUpdate");
        ForceUpdateButton.Content = Loc.T("ForceUpdate");
        UpdateInstallButton.Content = Loc.T("UpdInstall");
        if (pendingUpdate != null) UpdateBannerText.Text = Loc.T("UpdAvailable", pendingUpdate.Tag.TrimStart('v'));
        RenderStatus();
    }

    void Language_Click(object sender, RoutedEventArgs e)
    {
        settings.Language = Loc.Language = (string)((RadioButton)sender).Tag;
        settings.Save();
        ApplyLanguage();
    }

    // ------------------------------------------------------------------ état de la liaison

    void OnStatus(EngineStatus s)
    {
        status = s;
        RenderStatus();
    }

    void RenderStatus()
    {
        bool sender = settings.Role == Role.Sender;
        string peer = status.Peer ?? settings.TargetName ?? "…";
        string title, sub;
        Brush color;

        switch (status.State)
        {
            case LinkState.Connected:
                color = Green;
                title = Loc.T(sender ? "SendingTitle" : "ReceivingTitle");
                sub = sender ? Loc.T("SendingSub", peer) : Loc.T("ReceivingSub", peer, status.BufferMs);
                break;
            case LinkState.Waiting:
                color = Orange;
                title = Loc.T("WaitingTitle");
                sub = sender ? Loc.T("WaitingSender", peer) : Loc.T("WaitingReceiver");
                break;
            case LinkState.Error:
                color = Red;
                title = Loc.T("ErrorTitle");
                sub = Loc.T(status.ErrorKey ?? "ErrGeneric");
                break;
            default:
                color = Gray;
                title = Loc.T("StoppedTitle");
                sub = Loc.T(sender ? "StoppedSender" : "StoppedReceiver");
                break;
        }

        StatusTitle.Text = title;
        StatusSub.Text = sub;
        IconLeft.Fill = IconRight.Fill = IconWave.Stroke = color;

        bool running = engine.Running;
        StartButton.Content = Loc.T(running ? "Stop" : "Start");
        StartButton.Style = (Style)FindResource(running ? "Btn" : "BtnPrimary");
        tray.Update(status.State, $"BtAudioLink · {title}", running, settings.Role);
    }

    EngineOptions Options() => new()
    {
        Role = settings.Role,
        TargetAddress = settings.TargetAddress,
        MuteLocal = settings.MuteLocal,
        MarginMs = settings.MarginMs,
        Volume = settings.Volume / 100f,
    };

    void StartEngine()
    {
        settings.Running = true;
        settings.Save();
        engine.Start(Options());
        RenderStatus();
    }

    void StopEngine()
    {
        settings.Running = false;
        settings.Save();
        engine.Stop();
        status = new EngineStatus(LinkState.Stopped, settings.Role, null, null, 0, 0);
        RenderStatus();
    }

    public void ToggleRun()
    {
        if (engine.Running) StopEngine();
        else StartEngine();
    }

    public void SetRole(Role role)
    {
        if (settings.Role == role) return;
        settings.Role = role;
        settings.Save();
        SenderRadio.IsChecked = role == Role.Sender;
        ReceiverRadio.IsChecked = role == Role.Receiver;
        UpdateRoleSections();
        if (engine.Running) engine.Start(Options());        // redémarre la liaison dans le nouveau rôle
        else status = new EngineStatus(LinkState.Stopped, role, null, null, 0, 0);
        RenderStatus();
    }

    void StartButton_Click(object sender, RoutedEventArgs e) => ToggleRun();

    void Role_Click(object sender, RoutedEventArgs e) => SetRole(SenderRadio.IsChecked == true ? Role.Sender : Role.Receiver);

    void Autostart_Click(object sender, RoutedEventArgs e) => Settings.Autostart = AutostartSwitch.IsChecked == true;

    // ------------------------------------------------------------------ réglages

    void UpdateRoleSections()
    {
        SenderSettings.Visibility = settings.Role == Role.Sender ? Visibility.Visible : Visibility.Collapsed;
        ReceiverSettings.Visibility = settings.Role == Role.Receiver ? Visibility.Visible : Visibility.Collapsed;
    }

    async void LoadDevices()
    {
        if (settings.Role != Role.Sender) return;
        var devices = await Task.Run(Engine.ListPaired);
        DeviceList.Children.Clear();
        foreach (var d in devices)
        {
            var row = new RadioButton
            {
                Content = d.Name,
                Tag = d,
                GroupName = "Device",
                Style = (Style)FindResource("DeviceRow"),
                IsChecked = string.Equals(d.Address, settings.TargetAddress, StringComparison.OrdinalIgnoreCase),
            };
            row.Click += Device_Click;
            DeviceList.Children.Add(row);
        }
        NoDeviceText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => LoadDevices();

    void Device_Click(object sender, RoutedEventArgs e)
    {
        var d = (PairedDevice)((RadioButton)sender).Tag;
        settings.TargetAddress = d.Address;
        settings.TargetName = d.Name;
        settings.Save();
        if (engine.Running && settings.Role == Role.Sender) engine.Start(Options());
        RenderStatus();
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        settings.MuteLocal = MuteSwitch.IsChecked == true;
        settings.Save();
        if (engine.Running && settings.Role == Role.Sender) engine.Start(Options());
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (settings == null) return;          // appelé pendant la construction de la fenêtre
        settings.Volume = (int)Math.Round(e.NewValue);
        engine.SetLive(settings.MarginMs, settings.Volume / 100f);
    }

    void Profile_Click(object sender, RoutedEventArgs e)
    {
        settings.Stable = StableRadio.IsChecked == true;
        settings.Save();
        engine.SetLive(settings.MarginMs, settings.Volume / 100f);
    }

    // ------------------------------------------------------------------ mise à jour

    void SetUpdateStatus(string text)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Visibility = Visibility.Visible;
    }

    async Task CheckUpdateAsync(bool silent, bool force)
    {
        if (updating) return;
        try
        {
            if (!silent) SetUpdateStatus(Loc.T("UpdChecking"));
            var latest = await Updater.GetLatestAsync();
            if (latest == null)
            {
                if (!silent) SetUpdateStatus(Loc.T("UpdNoRelease"));
                return;
            }

            if (force)
            {
                await InstallAsync(latest);
            }
            else if (Updater.IsNewer(latest))
            {
                pendingUpdate = latest;
                string text = Loc.T("UpdAvailable", latest.Tag.TrimStart('v'));
                UpdateBannerText.Text = text;
                UpdateBanner.Visibility = Visibility.Visible;
                if (!silent) SetUpdateStatus(text);
            }
            else if (!silent) SetUpdateStatus(Loc.T("UpdUpToDate"));
        }
        catch (Exception ex)
        {
            Log.Write($"Recherche de mise à jour : {ex.Message}");
            if (!silent) SetUpdateStatus(Loc.T("UpdFailed", ex.Message));
        }
    }

    async Task InstallAsync(Updater.Release release)
    {
        if (updating) return;
        updating = true;
        CheckUpdateButton.IsEnabled = ForceUpdateButton.IsEnabled = UpdateInstallButton.IsEnabled = false;
        try
        {
            void Progress(int percent) => Dispatcher.BeginInvoke(() =>
            {
                string text = Loc.T("UpdDownloading", percent);
                SetUpdateStatus(text);
                UpdateBannerText.Text = text;
            });
            string file = await Updater.DownloadAsync(release, Progress);

            // On garde « Running » tel quel : la nouvelle version reprendra la liaison toute seule.
            settings.Save();
            engine.Stop();
            Updater.ApplyAndRestart(file, minimized: !IsVisible);
            reallyClosing = true;
            tray.Dispose();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Write($"Mise à jour : {ex}");
            SetUpdateStatus(Loc.T("UpdFailed", ex.Message));
            if (pendingUpdate != null) UpdateBannerText.Text = Loc.T("UpdAvailable", pendingUpdate.Tag.TrimStart('v'));
            updating = false;
            CheckUpdateButton.IsEnabled = ForceUpdateButton.IsEnabled = UpdateInstallButton.IsEnabled = true;
            if (settings.Running && !engine.Running) engine.Start(Options());
        }
    }

    async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync(silent: false, force: false);

    async void ForceUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync(silent: false, force: true);

    async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (pendingUpdate != null) await InstallAsync(pendingUpdate);
    }
}
