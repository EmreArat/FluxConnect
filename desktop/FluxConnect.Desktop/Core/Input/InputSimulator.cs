using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FluxConnect.Desktop.Core.Input;

/// <summary>
/// Windows SendInput API kullanarak fare ve klavye olaylarını sisteme enjekte eder.
/// </summary>
public static class InputSimulator
{
    // ============================================================
    // Win32 Yapıları
    // ============================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Sabitler
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    // Modifier bit maskesi (viewer ile ortak protokol)
    public const int MOD_CTRL = 1;
    public const int MOD_ALT = 2;
    public const int MOD_SHIFT = 4;
    public const int MOD_WIN = 8;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // ============================================================
    // Fare İşlemleri
    // ============================================================

    /// <summary>
    /// Fareyi ekrandaki oransal konuma (0.0-1.0) taşır.
    /// </summary>
    public static void MoveMouse(double xRatio, double yRatio)
    {
        // MOUSEEVENTF_ABSOLUTE: 0-65535 arası koordinat sistemi
        int x = (int)(xRatio * 65535);
        int y = (int)(yRatio * 65535);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = x,
                    dy = y,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void LeftClick() => MouseEvent(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP);
    public static void LeftDown() => MouseEvent(MOUSEEVENTF_LEFTDOWN);
    public static void LeftUp() => MouseEvent(MOUSEEVENTF_LEFTUP);
    public static void RightClick() => MouseEvent(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP);
    public static void MiddleClick() => MouseEvent(MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MIDDLEUP);

    public static void Scroll(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = (uint)delta,
                    dwFlags = MOUSEEVENTF_WHEEL
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void MouseEvent(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    // ============================================================
    // Klavye İşlemleri
    // ============================================================

    public static void KeyDown(ushort virtualKey)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = KEYEVENTF_KEYDOWN }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void KeyUp(ushort virtualKey)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = KEYEVENTF_KEYUP }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Unicode karakter gönder (yazı için).
    /// </summary>
    public static void SendChar(char c)
    {
        var inputs = new INPUT[]
        {
            new() {
                type = INPUT_KEYBOARD,
                U = new InputUnion {
                    ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE }
                }
            },
            new() {
                type = INPUT_KEYBOARD,
                U = new InputUnion {
                    ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
                }
            }
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Metni Unicode olarak gönderir. Klavye düzeninden bağımsızdır, bu yüzden
    /// AltGr ile yazılan karakterler (@ { } \ €) ve Türkçe harfler doğru çıkar.
    /// </summary>
    public static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(text[i], false);
            inputs[i * 2 + 1] = UnicodeInput(text[i], true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0)
            }
        }
    };

    /// <summary>
    /// Tuşu modifier'larıyla birlikte tek seferde basıp bırakır (ör. Ctrl+C).
    /// Bas ve bırak aynı çağrıda gittiği için modifier basılı kalamaz.
    /// </summary>
    public static void SendCombo(ushort virtualKey, int modifiers)
    {
        var mods = new List<ushort>(4);
        if ((modifiers & MOD_CTRL) != 0) mods.Add(VK_CONTROL);
        if ((modifiers & MOD_ALT) != 0) mods.Add(VK_MENU);
        if ((modifiers & MOD_SHIFT) != 0) mods.Add(VK_SHIFT);
        if ((modifiers & MOD_WIN) != 0) mods.Add(VK_LWIN);

        var inputs = new List<INPUT>(mods.Count * 2 + 2);
        foreach (var m in mods) inputs.Add(KeyInput(m, false));
        inputs.Add(KeyInput(virtualKey, false));
        inputs.Add(KeyInput(virtualKey, true));
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(KeyInput(mods[i], true));

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Basılı kalmış olabilecek tüm modifier tuşlarını bırakır.
    /// Odak kaybı veya oturum sonunda "Ctrl takılı kaldı" durumunu temizler.
    /// </summary>
    public static void ReleaseModifiers()
    {
        ushort[] keys =
        [
            VK_LCONTROL, VK_RCONTROL, VK_CONTROL,
            VK_LMENU, VK_RMENU, VK_MENU,
            VK_LSHIFT, VK_RSHIFT, VK_SHIFT,
            VK_LWIN, VK_RWIN
        ];

        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            inputs[i] = KeyInput(keys[i], true);

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : KEYEVENTF_KEYDOWN }
        }
    };
}
