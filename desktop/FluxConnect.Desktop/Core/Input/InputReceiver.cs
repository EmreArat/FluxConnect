using System.Text.Json;
using FluxConnect.Desktop.Core.Capture;

namespace FluxConnect.Desktop.Core.Input;

/// <summary>
/// Gelen JSON komut mesajlarını InputSimulator'a yönlendirir.
/// </summary>
public static class InputReceiver
{
    /// <summary>
    /// Relay üzerinden gelen base64 JSON verisini ayrıştırır ve gereğini yapar.
    /// Format: { "t": "mm"|"mc"|"mdc"|"mw"|"kd"|"ku"|"kc"|"kx"|"krel"|"scr", ... }
    /// </summary>
    public static void Handle(string base64Data)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Data));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("t", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                // Fare hareketi
                case "mm":
                    InputSimulator.MoveMouse(
                        root.GetProperty("x").GetDouble(),
                        root.GetProperty("y").GetDouble());
                    break;

                // Fare tıklama
                case "mc":
                    var btn = root.GetProperty("b").GetString();
                    var down = root.GetProperty("d").GetBoolean();
                    switch (btn)
                    {
                        case "L": if (down) InputSimulator.LeftDown(); else InputSimulator.LeftUp(); break;
                        case "R": if (down) InputSimulator.RightClick(); break;
                        case "M": InputSimulator.MiddleClick(); break;
                    }
                    break;

                // Fare çift tıklama
                case "mdc":
                    InputSimulator.LeftClick();
                    System.Threading.Thread.Sleep(50);
                    InputSimulator.LeftClick();
                    break;

                // Scroll
                case "mw":
                    InputSimulator.Scroll(root.GetProperty("d").GetInt32() * 120);
                    break;

                // Klavye bas
                case "kd":
                    InputSimulator.KeyDown((ushort)root.GetProperty("k").GetInt32());
                    break;

                // Klavye bırak
                case "ku":
                    InputSimulator.KeyUp((ushort)root.GetProperty("k").GetInt32());
                    break;

                // Metin gönder (Unicode, klavye düzeninden bağımsız)
                case "kc":
                    var ch = root.GetProperty("c").GetString();
                    if (!string.IsNullOrEmpty(ch)) InputSimulator.SendText(ch);
                    break;

                // Tuş + modifier kombinasyonu, bas-bırak tek seferde
                case "kx":
                    var comboMods = root.TryGetProperty("m", out var modProp) ? modProp.GetInt32() : 0;
                    InputSimulator.SendCombo((ushort)root.GetProperty("k").GetInt32(), comboMods);
                    break;

                // Basılı kalmış modifier'ları temizle
                case "krel":
                    InputSimulator.ReleaseModifiers();
                    break;

                // Ekran değiştir ("scr" = screen)
                case "scr":
                    var screenIdx = root.GetProperty("i").GetInt32();
                    App.Session.SwitchScreen(screenIdx);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [InputReceiver] Hata: {ex.Message}\n");
        }
    }
}
