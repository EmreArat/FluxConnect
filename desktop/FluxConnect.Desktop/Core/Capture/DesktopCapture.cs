using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace FluxConnect.Desktop.Core.Capture;

/// <summary>
/// DXGI Desktop Duplication API kullanarak ekranı yüksek performansla yakalar.
/// </summary>
public class DesktopCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private IDXGIOutputDuplication? _deskDupl;
    private ID3D11Texture2D? _stagingTexture;

    private int _width;
    private int _height;
    private bool _initialized;
    private CancellationTokenSource? _captureCts;
    private int _targetScreenIndex = 0;

    public int Width => _width;
    public int Height => _height;
    public bool IsCapturing => _captureCts != null && !_captureCts.IsCancellationRequested;

    /// <summary>
    /// Ekranın yeni bir karesi yakalandığında tetiklenir.
    /// Bytes: Yakalanan piksellerin ham RGBA kopyası
    /// RowPitch: Bir satırdaki byte sayısı (genellikle Genişlik * 4)
    /// </summary>
    public event Action<byte[], int, int, int>? OnFrameCaptured;

    public DesktopCapture()
    {
    }

    /// <summary>
    /// DXGI ve D3D11 cihazlarını başlatır. Ekran çözünürlüğünü alır.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        try
        {
            // 1. Monitör 'DeviceName'ini WinForms API'si üzerinden referans alıyoruz (DPI Bağımsız)
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (_targetScreenIndex < 0 || _targetScreenIndex >= screens.Length) _targetScreenIndex = 0;
            var targetDeviceName = screens[_targetScreenIndex].DeviceName;

            // 2. DXGI ile aynı DeviceName'e sahip ekran çıkışını bul
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter? selectedAdapter = null;
            IDXGIOutput? selectedOutput = null;

            for (uint i = 0; factory.EnumAdapters(i, out var tempAdapter).Success; i++)
            {
                for (uint j = 0; tempAdapter.EnumOutputs(j, out var tempOutput).Success; j++)
                {
                    var outputName = new string(tempOutput.Description.DeviceName).TrimEnd('\0');
                    if (string.Equals(outputName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedAdapter = tempAdapter;
                        selectedOutput = tempOutput;
                        break;
                    }
                    tempOutput.Dispose();
                }
                if (selectedAdapter != null) break;
                tempAdapter.Dispose();
            }

            if (selectedAdapter == null || selectedOutput == null)
                throw new Exception($"Ekran '{targetDeviceName}' ile eşleşen grafik çıkışı bulunamadı!");

            // 2. D3D11 Cihazı Doğru Adaptörle Oluştur
            var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            
            // Eğer belirli bir adapter kullanıyorsak DriverType.Unknown olmalıdır.
            D3D11.D3D11CreateDevice(
                selectedAdapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out _device,
                out _deviceContext).CheckError();

            if (_device == null || _deviceContext == null)
                throw new Exception("D3D11 cihazı oluşturulamadı.");

            // 3. DXGI Output ve Duplication Başlat
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var dxgiOutput1 = selectedOutput.QueryInterface<IDXGIOutput1>();

            _deskDupl = dxgiOutput1.DuplicateOutput(dxgiDevice);

            // Çözünürlük bilgisini al
            var desc = selectedOutput.Description;
            _width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            _height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            
            // Seçilen bileşenleri temizle
            selectedOutput.Dispose();
            selectedAdapter.Dispose();

            // 4. Staging Texture (CPU'nun VRAM'den okuyabileceği alan) oluştur
            var texDesc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, // Vortice'de CPUAccessFlags'dir (büyük harf D3D11 namings)
                MiscFlags = ResourceOptionFlags.None
            };

            _stagingTexture = _device.CreateTexture2D(texDesc);
            _initialized = true;

            System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [Capture] Başlatıldı: {_width}x{_height}\n");
        }
        catch (Exception ex)
        {
            Dispose();
            throw new Exception($"Ekran yakalama başlatılamadı (Belki de ekran kilitli/RDP): {ex.Message}");
        }
    }

    /// <summary>
    /// Belirtilen FPS hedefi ile sürekli yakalama döngüsünü başlatır.
    /// </summary>
    public void Start(int targetFps = 30)
    {
        if (!_initialized) throw new InvalidOperationException("Önce Initialize() çağrılmalı.");
        if (IsCapturing) return;

        _captureCts = new CancellationTokenSource();
        int waitTimeMs = 1000 / targetFps;

        Task.Run(async () =>
        {
            var sw = new Stopwatch();
            var ct = _captureCts.Token;

            while (!ct.IsCancellationRequested)
            {
                sw.Restart();
                
                try
                {
                    TryCaptureFrame();
                }
                catch (SharpGenException vex) when (vex.ResultCode.Code == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT
                {
                    // Ekranda değişiklik yok. Ancak FPS gereği eldeki son başarılı resmi göndermeliyiz.
                    // Aksi takdirde, ekran değiştiğinde ve yeni ekranda hareket yoksa donmuş gibi görünür.
                    SendLastFrame();
                }
                catch (SharpGenException vex) when (vex.ResultCode.Code == unchecked((int)0x887A0026)) // DXGI_ERROR_ACCESS_LOST
                {
                    // Ekran mod değiştirdi (UAC, çözünürlük vs). Yeniden başlatılmalı.
                    System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [Capture] Erişim kayboldu, yeniden denenecek...\n");
                    Reinitialize();
                }
                catch (Exception ex)
                {
                    System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [Capture] Hata: {ex.ToString()}\n");
                }

                // FPS hedefine uymak için bekle
                sw.Stop();
                int elapsed = (int)sw.ElapsedMilliseconds;
                if (elapsed < waitTimeMs)
                {
                    await Task.Delay(waitTimeMs - elapsed, ct).ConfigureAwait(false);
                }
            }
        }, _captureCts.Token);
    }

    public void Stop()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;
    }

    /// <summary>
    /// Ekran yakalamayı durdurup yeni monitöre uygun şekilde motoru yeniden başlatır.
    /// </summary>
    public void SwitchScreen(int screenIndex)
    {
        if (_targetScreenIndex == screenIndex) return;
        _targetScreenIndex = screenIndex;
        // DXGI yakalayıcısı donanıma ve output'a direkt bağlı olduğundan
        // motoru yeniden oluşturmak zorundayız.
        Reinitialize();
    }

    /// <summary>
    /// Bir kareyi VRAM'den çekip CPU RAM'ine indirir (Staging Texture üzerinden).
    /// </summary>
    private void TryCaptureFrame()
    {
        if (_deskDupl == null || _deviceContext == null || _stagingTexture == null) return;

        // 10 ms bekle (Aksi halde çerçeve yoksa DXGI_ERROR_WAIT_TIMEOUT atar)
        var result = _deskDupl.AcquireNextFrame(10, out var frameInfo, out var desktopResource);

        if (result.Failure)
        {
            result.CheckError(); // Hata varsa fırlat (timeout dahil)
            return;
        }

        try
        {
            // VRAM'deki resmi Staging Texture'a kopyala (CPU okuması için)
            using var texture2D = desktopResource.QueryInterface<ID3D11Texture2D>();
            _deviceContext.CopyResource(_stagingTexture, texture2D);

            // CPU hafızasına haritala (Map)
            var mappedResource = _deviceContext.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            
            try
            {
                int rowPitch = (int)mappedResource.RowPitch;
                int totalBytes = rowPitch * _height;

                // Görüntüyü byte dizisine kopyala
                byte[] rawData = new byte[totalBytes];
                Marshal.Copy(mappedResource.DataPointer, rawData, 0, totalBytes);

                // Dışarıya gönder
                OnFrameCaptured?.Invoke(rawData, _width, _height, rowPitch);
            }
            finally
            {
                // İşimiz bitince haritayı çöz
                _deviceContext.Unmap(_stagingTexture, 0);
            }
        }
        finally
        {
            desktopResource.Dispose();
            _deskDupl.ReleaseFrame();
        }
    }

    /// <summary>
    /// Değişiklik olmadığında eldeki son kopyalanmış StagingTexture'ı gönderir.
    /// </summary>
    private void SendLastFrame()
    {
        if (_deviceContext == null || _stagingTexture == null) return;

        try
        {
            var mappedResource = _deviceContext.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowPitch = (int)mappedResource.RowPitch;
                int totalBytes = rowPitch * _height;

                byte[] rawData = new byte[totalBytes];
                Marshal.Copy(mappedResource.DataPointer, rawData, 0, totalBytes);

                OnFrameCaptured?.Invoke(rawData, _width, _height, rowPitch);
            }
            finally
            {
                _deviceContext.Unmap(_stagingTexture, 0);
            }
        }
        catch { /* Hata olursa pass geç, bir dahaki sefere alır */ }
    }

    private void Reinitialize()
    {
        Stop();
        DisposeInternal();
        Thread.Sleep(500); // Biraz bekle (mode switch zaman alır)
        try
        {
            Initialize();
            Start();
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [Capture] Reinitialize Hatası: {ex.Message}\n");
        }
    }

    private void DisposeInternal()
    {
        _deskDupl?.Dispose(); _deskDupl = null;
        _stagingTexture?.Dispose(); _stagingTexture = null;
        _deviceContext?.Dispose(); _deviceContext = null;
        _device?.Dispose(); _device = null;
        _initialized = false;
    }

    public void Dispose()
    {
        Stop();
        DisposeInternal();
    }
}
