using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FluxConnect.Desktop.Core.Input;

/// <summary>
/// Basılan tuşu, yerel klavye düzenine göre yazılacak metne çevirir.
/// Böylece AltGr (@ { } \ €) ve Türkçe harfler karşı tarafa doğru gider.
/// </summary>
public static class KeyboardTranslator
{
    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    private const uint MAPVK_VK_TO_VSC = 0;

    // Windows 10 1607+: klavyenin dead-key durumunu bozmadan çevir
    private const uint TOUNICODE_NO_STATE_CHANGE = 0x4;

    /// <summary>
    /// Tuşun ürettiği metni döndürür. Yazı üretmiyorsa, ölü tuşsa (^ ¨ gibi)
    /// veya kontrol karakteriyse null döner; bu durumda tuş kodu yolu kullanılmalıdır.
    /// </summary>
    public static string? TryGetText(int virtualKey)
    {
        try
        {
            var layout = GetKeyboardLayout(0);
            var scanCode = MapVirtualKeyEx((uint)virtualKey, MAPVK_VK_TO_VSC, layout);

            var state = new byte[256];
            if (!GetKeyboardState(state)) return null;

            var buffer = new StringBuilder(8);
            var count = ToUnicodeEx((uint)virtualKey, scanCode, state, buffer,
                buffer.Capacity, TOUNICODE_NO_STATE_CHANGE, layout);

            // 0 = karakter üretmiyor, negatif = ölü tuş
            if (count <= 0) return null;

            var text = buffer.ToString(0, count);
            foreach (var c in text)
                if (char.IsControl(c)) return null;

            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
