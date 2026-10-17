using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneRemote.Input;

/// <summary>Lock, sleep, restart or shut down the PC (the phone asks for confirmation first).</summary>
public static class PowerActions
{
    public static void Run(string action)
    {
        // A short delay lets the reply reach the phone before the PC goes away.
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            switch (action)
            {
                case "lock":
                    LockWorkStation();
                    break;
                case "sleep":
                    SetSuspendState(false, false, false);
                    break;
                case "restart":
                    Shutdown("/r");
                    break;
                case "shutdown":
                    Shutdown("/s");
                    break;
            }
        });
    }

    // Not forced: an app with unsaved work can still ask to save before Windows closes.
    private static void Shutdown(string mode)
    {
        var psi = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add("0");
        Process.Start(psi)?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
