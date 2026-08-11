# FluxConnect

Güvenli uzaktan masaüstü bağlantı uygulaması (Windows). LAN ve internet (relay) modlarını destekler.

## Gereksinimler

- Windows 10/11 (64-bit)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)

## Kurulum

1. [Releases](https://github.com/EmreArat/FluxConnect/releases) sayfasından `FluxConnect.exe` indirin.
2. Çalıştırın — kurulum sihirbazı gerekmez.

## Özellikler

- Uzak ekran, ses, webcam, dosya transferi
- LAN (doğrudan) ve internet (relay) bağlantısı
- Opsiyonel oturum şifresi (alternatif kabul yolu)
- Sistem tepsisi + Windows ile başlatma
- Opsiyonel GitHub üzerinden güncelleme

## Relay sunucusu

```bash
cd relay
npm install
npm run build
npm start
```

## Geliştirme

```powershell
dotnet run --project desktop/FluxConnect.Desktop/FluxConnect.Desktop.csproj
```

Publish:

```powershell
dotnet publish desktop/FluxConnect.Desktop/FluxConnect.Desktop.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o desktop/FluxConnect.Desktop/publish_output
```

## Lisans

Proprietary — FluxHub
