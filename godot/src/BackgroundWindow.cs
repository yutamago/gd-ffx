using System;
using System.Runtime.InteropServices;
using Godot;

namespace GdFfx;

/// <summary>
/// <c>--background-window</c> (for checks on the GPU from WSL, tools/compare-shot-win.sh): the
/// window opens without taking the focus and is put behind every other window, so a check run
/// while someone works does not come to the front (Godot moves an off-screen window back onto
/// the screen, and a minimised one is not drawn). Windows only; elsewhere nothing.
/// </summary>
public static class BackgroundWindow
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr Bottom = new(1); // HWND_BOTTOM
    private const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;

    /// <summary>Whether the flag was given.</summary>
    public static bool On => GameSettings.Flag("background-window");

    /// <summary>Sends the main window behind the others (each call: a window brought up again goes back).</summary>
    public static void Apply()
    {
        if (!On || OS.GetName() != "Windows")
            return;
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        var hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, Bottom, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
    }
}
