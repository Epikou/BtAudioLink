using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace BtAudioLink;

/// <summary>
/// Navigation à la manette (consoles portables) : croix ou stick gauche pour se déplacer, A pour valider,
/// B pour réduire la fenêtre. Actif seulement quand la fenêtre est au premier plan.
/// </summary>
public sealed class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public XInputGamepad Pad;
    }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint index, out XInputState state);

    const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, A = 0x1000, B = 0x2000;
    const short StickThreshold = 20000;

    readonly MainWindow window;
    readonly DispatcherTimer timer;
    ushort previous;
    bool available = true;

    public Gamepad(MainWindow window)
    {
        this.window = window;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    void Poll()
    {
        if (!available || !window.IsActive) { previous = 0; return; }

        ushort buttons = 0;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out var s) != 0) continue;
                buttons |= s.Pad.Buttons;
                if (s.Pad.LeftY > StickThreshold) buttons |= Up;
                if (s.Pad.LeftY < -StickThreshold) buttons |= Down;
                if (s.Pad.LeftX < -StickThreshold) buttons |= Left;
                if (s.Pad.LeftX > StickThreshold) buttons |= Right;
            }
        }
        catch (Exception)
        {
            available = false;          // XInput absent : on ne réessaie pas
            return;
        }

        ushort pressed = (ushort)(buttons & ~previous);
        previous = buttons;
        if (pressed == 0) return;

        var focused = Keyboard.FocusedElement as UIElement;

        // Sur le curseur de volume, gauche/droite règlent la valeur au lieu de déplacer la sélection.
        if (focused is Slider slider && (pressed & (Left | Right)) != 0)
        {
            slider.Value += (pressed & Right) != 0 ? slider.LargeChange : -slider.LargeChange;
            return;
        }

        if ((pressed & Up) != 0) Move(focused, FocusNavigationDirection.Up);
        else if ((pressed & Down) != 0) Move(focused, FocusNavigationDirection.Down);
        else if ((pressed & Left) != 0) Move(focused, FocusNavigationDirection.Left);
        else if ((pressed & Right) != 0) Move(focused, FocusNavigationDirection.Right);
        else if ((pressed & A) != 0) Activate(focused);
        else if ((pressed & B) != 0) window.HideToTray();
    }

    void Move(UIElement? focused, FocusNavigationDirection direction)
    {
        if (focused == null || !focused.IsVisible)
            window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        else
            focused.MoveFocus(new TraversalRequest(direction));
    }

    static void Activate(UIElement? focused)
    {
        switch (focused)
        {
            case RadioButton radio:
                radio.IsChecked = true;
                radio.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case CheckBox check:
                check.IsChecked = check.IsChecked != true;
                check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case ButtonBase button:
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
        }
    }
}
