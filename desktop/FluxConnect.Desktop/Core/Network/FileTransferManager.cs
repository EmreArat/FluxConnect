using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FluxConnect.Desktop.Core.Network;

public class FileTransferManager : IDisposable
{
    private const int ChunkSize = 256 * 1024; // 256KB parça boyutu

    private CancellationTokenSource? _sendCts;
    private FileStream? _receiveStream;
    private string? _receivingFilePath;
    private int _expectedChunks;
    private int _receivedChunks;

    public event Action<string, double>? OnProgress;
    public event Action<string>? OnSendCompleted;
    public event Action<string>? OnReceiveCompleted;
    public event Action<string>? OnError;

    public event Action<string>? OnDataToSend; 

    public async Task SendFileAsync(string filePath, string targetPath = "")
    {
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;

        try
        {
            var fileInfo = new FileInfo(filePath);
            var totalBytes = fileInfo.Length;
            var totalChunks = (int)Math.Ceiling((double)totalBytes / ChunkSize);
            var fileName = fileInfo.Name;

            var targetPathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(targetPath ?? ""));
            
            // Başlangıç mesajını yolla
            var startMsg = $"FIL:START:{fileName}:{totalBytes}:{totalChunks}:{targetPathBase64}";
            OnDataToSend?.Invoke(startMsg);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var buffer = new byte[ChunkSize];
            int bytesRead;
            int chunkIndex = 0;

            while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                var base64 = Convert.ToBase64String(buffer, 0, bytesRead);
                var chunkMsg = $"FIL:CHUNK:{chunkIndex}:{base64}";
                OnDataToSend?.Invoke(chunkMsg);

                chunkIndex++;
                double progress = (double)chunkIndex / totalChunks * 100;
                OnProgress?.Invoke($"Gönderiliyor: {fileName}", progress);

                // Bant genişliğini tıkamamak için her parça sonrası çok ufak bekliyoruz
                await Task.Delay(10, token); 
            }

            OnDataToSend?.Invoke($"FIL:END:{fileName}");
            OnSendCompleted?.Invoke(fileName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnError?.Invoke($"Gönderim hatası: {ex.Message}");
        }
    }

    public void CancelTransfer()
    {
        _sendCts?.Cancel();
    }

    public void HandleIncomingMessage(string msg)
    {
        try
        {
            if (msg.StartsWith("FIL:START:"))
            {
                var parts = msg.Split(':', 6);
                if (parts.Length >= 5)
                {
                    var fileName = parts[2];
                    var totalChunks = int.Parse(parts[4]);
                    
                    var targetDir = "";
                    if (parts.Length == 6 && !string.IsNullOrEmpty(parts[5]))
                    {
                        targetDir = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[5]));
                    }

                    if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                    {
                        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        targetDir = Path.Combine(desktopDir, "FluxConnect Alınan Dosyalar");
                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                    }

                    _receivingFilePath = Path.Combine(targetDir, fileName);
                    
                    // Dosya zaten varsa yeni isim oluştur
                    int i = 1;
                    while (File.Exists(_receivingFilePath))
                    {
                        var nameOnly = Path.GetFileNameWithoutExtension(fileName);
                        var ext = Path.GetExtension(fileName);
                        _receivingFilePath = Path.Combine(targetDir, $"{nameOnly} ({i}){ext}");
                        i++;
                    }

                    _receiveStream = new FileStream(_receivingFilePath, FileMode.Create, FileAccess.Write);
                    _expectedChunks = totalChunks;
                    _receivedChunks = 0;

                    OnProgress?.Invoke($"Alınıyor: {Path.GetFileName(_receivingFilePath)}", 0);
                }
            }
            else if (msg.StartsWith("FIL:CHUNK:"))
            {
                var parts = msg.Split(':', 4);
                if (parts.Length >= 4 && _receiveStream != null)
                {
                    var base64 = parts[3];
                    var bytes = Convert.FromBase64String(base64);

                    _receiveStream.Write(bytes, 0, bytes.Length);
                    _receivedChunks++;

                    double progress = (double)_receivedChunks / _expectedChunks * 100;
                    if (progress > 100) progress = 100;

                    string name = Path.GetFileName(_receivingFilePath ?? "");
                    OnProgress?.Invoke($"Alınıyor: {name}", progress);
                }
            }
            else if (msg.StartsWith("FIL:END:"))
            {
                _receiveStream?.Dispose();
                _receiveStream = null;

                if (_receivingFilePath != null)
                {
                    string name = Path.GetFileName(_receivingFilePath);
                    OnReceiveCompleted?.Invoke(name);
                }
            }
        }
        catch (Exception ex)
        {
            _receiveStream?.Dispose();
            _receiveStream = null;
            OnError?.Invoke($"Alma işlemi hatası: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _receiveStream?.Dispose();
    }
}
