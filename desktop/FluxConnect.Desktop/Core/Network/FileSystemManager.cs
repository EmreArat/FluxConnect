using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;

namespace FluxConnect.Desktop.Core.Network;

public class FileSystemNode
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
}

public class FileSystemManager
{
    public event Action<string>? OnDataToSend;
    public event Action<string, List<FileSystemNode>>? OnRemoteDirectoryReceived;
    public event Action<string>? OnRemoteError;
    public event Action<string, string>? OnFileRequested; // arg1: remoteFilePath, arg2: localDestPath

    public void HandleIncomingCommand(string data)
    {
        try
        {
            if (data == "FS:REQ_DRIVES")
            {
                var drives = new List<FileSystemNode>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try 
                    {
                        var name = d.IsReady ? $"{d.Name} ({d.VolumeLabel})" : d.Name;
                        drives.Add(new FileSystemNode { Name = name, Path = d.Name, IsDirectory = true });
                    }
                    catch
                    {
                        drives.Add(new FileSystemNode { Name = d.Name, Path = d.Name, IsDirectory = true });
                    }
                }
                
                var json = JsonSerializer.Serialize(drives);
                var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
                OnDataToSend?.Invoke($"FS:RES_DRIVES:{base64}");
            }
            else if (data.StartsWith("FS:REQ_FILE:"))
            {
                var payload = data[12..];
                var splitIdx = payload.IndexOf('|');
                if (splitIdx > 0)
                {
                    var filePath = payload.Substring(0, splitIdx);
                    var base64Dest = payload.Substring(splitIdx + 1);
                    var destPath = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Dest));
                    OnFileRequested?.Invoke(filePath, destPath);
                }
            }
            else if (data.StartsWith("FS:REQ_DIR:"))
            {
                var targetDir = data[11..];
                var nodes = new List<FileSystemNode>();
                try
                {
                    var dirInfo = new DirectoryInfo(targetDir);
                    
                    // Önce Klasörler
                    foreach (var d in dirInfo.GetDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden)))
                    {
                        nodes.Add(new FileSystemNode { Name = d.Name, Path = d.FullName, IsDirectory = true, LastModified = d.LastWriteTime });
                    }
                    
                    // Sonra Dosyalar
                    foreach (var f in dirInfo.GetFiles().Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden)))
                    {
                        nodes.Add(new FileSystemNode { Name = f.Name, Path = f.FullName, IsDirectory = false, Size = f.Length, LastModified = f.LastWriteTime });
                    }

                    var json = JsonSerializer.Serialize(nodes);
                    var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
                    OnDataToSend?.Invoke($"FS:RES_DIR:{targetDir}|{base64}");
                }
                catch (UnauthorizedAccessException)
                {
                    OnDataToSend?.Invoke($"FS:ERR:Erişim reddedildi: {targetDir}");
                }
                catch (Exception ex)
                {
                    OnDataToSend?.Invoke($"FS:ERR:{ex.Message}");
                }
            }
            else if (data.StartsWith("FS:RES_DRIVES:"))
            {
                var base64 = data[14..];
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var nodes = JsonSerializer.Deserialize<List<FileSystemNode>>(json);
                if (nodes != null) OnRemoteDirectoryReceived?.Invoke("DRIVES", nodes);
            }
            else if (data.StartsWith("FS:RES_DIR:"))
            {
                var payload = data[11..];
                var splitIdx = payload.IndexOf('|');
                if (splitIdx > 0)
                {
                    var path = payload.Substring(0, splitIdx);
                    var base64 = payload.Substring(splitIdx + 1);
                    var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    var nodes = JsonSerializer.Deserialize<List<FileSystemNode>>(json);
                    if (nodes != null) OnRemoteDirectoryReceived?.Invoke(path, nodes);
                }
            }
            else if (data.StartsWith("FS:ERR:"))
            {
                OnRemoteError?.Invoke(data[7..]);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FS Handler Error: {ex.Message}");
        }
    }

    public void RequestRemoteDrives()
    {
        OnDataToSend?.Invoke("FS:REQ_DRIVES");
    }

    public void RequestRemoteDirectory(string path)
    {
        OnDataToSend?.Invoke($"FS:REQ_DIR:{path}");
    }

    public void RequestRemoteFile(string remoteFilePath, string localDestPath)
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(localDestPath));
        OnDataToSend?.Invoke($"FS:REQ_FILE:{remoteFilePath}|{base64}");
    }
}
