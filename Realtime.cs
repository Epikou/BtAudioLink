using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BtAudioLink;

/// <summary>
/// Réglages « temps réel » du processus. Une application fenêtrée réduite ou cachée derrière un jeu est bridée
/// par Windows 11 (mode efficacité, minuteries ralenties) : le son n'est alors plus capté ni envoyé à temps et
/// des morceaux sont perdus. On demande à Windows de ne pas brider ce processus.
/// </summary>
static class Realtime
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
    [DllImport("avrt.dll")] static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    const int ProcessPowerThrottling = 4;
    const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    /// <summary>À appeler une fois au démarrage.</summary>
    public static void ConfigureProcess()
    {
        try { timeBeginPeriod(1); } catch (Exception) { }
        try
        {
            // Masque de contrôle posé, état à zéro : on refuse explicitement le bridage de vitesse et celui des minuteries.
            var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
            if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>()))
            {
                // Windows 10 ne connaît pas le drapeau des minuteries : on retente avec la vitesse seule.
                state.ControlMask = ExecutionSpeed;
                SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            }
        }
        catch (Exception ex) { Log.Write($"Bridage Windows non désactivé : {ex.Message}"); }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch (Exception) { }
    }

    /// <summary>Inscrit le fil courant auprès du planificateur multimédia de Windows (priorité audio).</summary>
    public static IDisposable AudioThread()
    {
        try
        {
            uint index = 0;
            IntPtr handle = AvSetMmThreadCharacteristics("Pro Audio", ref index);
            if (handle != IntPtr.Zero) return new Revert(handle);
        }
        catch (Exception) { }
        try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch (Exception) { }
        return new Revert(IntPtr.Zero);
    }

    sealed class Revert(IntPtr handle) : IDisposable
    {
        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                try { AvRevertMmThreadCharacteristics(handle); } catch (Exception) { }
            }
        }
    }
}
