using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using FluxConnect.Desktop.Core.Network;
using System.Runtime.CompilerServices;

namespace FluxConnect.Desktop.UI;

public partial class FileExplorerWindow : Window
{
    private readonly FileSystemManager _fsManager;
    private readonly FileTransferManager _fileManager;

    public ObservableCollection<TreeNodeViewModel> LocalRoots { get; } = new();
    public ObservableCollection<TreeNodeViewModel> RemoteRoots { get; } = new();

    private TreeNodeViewModel? _selectedLocalNode;
    private TreeNodeViewModel? _selectedRemoteNode;

    public FileExplorerWindow(FileSystemManager fsManager, FileTransferManager fileManager)
    {
        InitializeComponent();
        _fsManager = fsManager;
        _fileManager = fileManager;

        LocalTreeView.ItemsSource = LocalRoots;
        RemoteTreeView.ItemsSource = RemoteRoots;

        LoadLocalDrives();

        _fsManager.OnRemoteDirectoryReceived += OnRemoteDirectoryReceived;
        _fsManager.OnRemoteError += OnRemoteError;

        this.Loaded += (s, e) => _fsManager.RequestRemoteDrives();
        this.Closed += (s, e) => 
        {
            _fsManager.OnRemoteDirectoryReceived -= OnRemoteDirectoryReceived;
            _fsManager.OnRemoteError -= OnRemoteError;
        };
    }

    // --- LOCAL Lojik ---
    private void LoadLocalDrives()
    {
        LocalRoots.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                var name = d.IsReady ? $"{d.Name} ({d.VolumeLabel})" : d.Name;
                var node = new TreeNodeViewModel { Name = "💽 " + name, Path = d.Name, IsDirectory = true };
                node.AddDummy();
                node.OnExpandRequested += Node_OnLocalExpandRequested;
                LocalRoots.Add(node);
            }
            catch
            {
                var node = new TreeNodeViewModel { Name = "💽 " + d.Name, Path = d.Name, IsDirectory = true };
                node.AddDummy();
                node.OnExpandRequested += Node_OnLocalExpandRequested;
                LocalRoots.Add(node);
            }
        }
    }

    private void Node_OnLocalExpandRequested(TreeNodeViewModel node)
    {
        try
        {
            node.Children.Clear();
            var dir = new DirectoryInfo(node.Path);
            foreach (var d in dir.GetDirectories().Where(x => !x.Attributes.HasFlag(FileAttributes.Hidden)))
            {
                var child = new TreeNodeViewModel { Name = "📁 " + d.Name, Path = d.FullName, IsDirectory = true };
                child.AddDummy();
                child.OnExpandRequested += Node_OnLocalExpandRequested;
                node.Children.Add(child);
            }
            foreach (var f in dir.GetFiles().Where(x => !x.Attributes.HasFlag(FileAttributes.Hidden)))
            {
                var child = new TreeNodeViewModel { Name = "📄 " + f.Name, Path = f.FullName, IsDirectory = false, Size = f.Length };
                node.Children.Add(child);
            }
        }
        catch { }
    }

    private void LocalTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedLocalNode = e.NewValue as TreeNodeViewModel;
        TxtLocalPath.Text = _selectedLocalNode?.Path ?? "Yerel Bilgisayar";
    }

    private void BtnLocalRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadLocalDrives();
    }

    // --- REMOTE Lojik ---
    private void OnRemoteDirectoryReceived(string path, List<FileSystemNode> nodes)
    {
        Dispatcher.Invoke(() =>
        {
            TxtRemotePath.Text = "Karşı Bilgisayar";

            if (path == "DRIVES")
            {
                RemoteRoots.Clear();
                foreach (var n in nodes)
                {
                    var viewNode = new TreeNodeViewModel { Name = "💽 " + n.Name, Path = n.Path, IsDirectory = n.IsDirectory };
                    if (n.IsDirectory)
                    {
                        viewNode.AddDummy();
                        viewNode.OnExpandRequested += Node_OnRemoteExpandRequested;
                    }
                    RemoteRoots.Add(viewNode);
                }
            }
            else
            {
                var targetNode = FindNodeByPath(RemoteRoots, path);
                if (targetNode != null)
                {
                    targetNode.Children.Clear();
                    foreach (var n in nodes)
                    {
                        var viewNode = new TreeNodeViewModel 
                        { 
                            Name = (n.IsDirectory ? "📁 " : "📄 ") + n.Name, 
                            Path = n.Path, 
                            IsDirectory = n.IsDirectory,
                            Size = n.Size
                        };
                        
                        if (n.IsDirectory)
                        {
                            viewNode.AddDummy();
                            viewNode.OnExpandRequested += Node_OnRemoteExpandRequested;
                        }
                        targetNode.Children.Add(viewNode);
                    }
                }
            }
        });
    }

    private TreeNodeViewModel? FindNodeByPath(IEnumerable<TreeNodeViewModel> nodes, string path)
    {
        // Path sonundaki slahları falan normalize etmek gerekebilir.
        foreach (var node in nodes)
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase) || 
                string.Equals(node.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return node;
            
            var found = FindNodeByPath(node.Children, path);
            if (found != null) return found;
        }
        return null;
    }

    private void Node_OnRemoteExpandRequested(TreeNodeViewModel node)
    {
        TxtRemotePath.Text = $"Yükleniyor: {node.Path}";
        _fsManager.RequestRemoteDirectory(node.Path);
    }

    private void OnRemoteError(string err)
    {
        Dispatcher.Invoke(() => 
        {
            TxtRemotePath.Text = "Hata! Karşı Bilgisayar";
            MessageBox.Show(err, "Erişim Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void RemoteTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedRemoteNode = e.NewValue as TreeNodeViewModel;
        // Dosya seçilirse üst klasörün yolunu vs görmek isteyebilir
        if (_selectedRemoteNode != null)
        {
            TxtRemotePath.Text = _selectedRemoteNode.Path;
        }
    }

    private void BtnRemoteRefresh_Click(object sender, RoutedEventArgs e)
    {
        TxtRemotePath.Text = "Karşı Bilgisayar (Yükleniyor...)";
        _fsManager.RequestRemoteDrives();
    }


    // --- TRANSFER BUTONLARI ---
    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocalNode == null || _selectedLocalNode.IsDirectory)
        {
            MessageBox.Show("Lütfen gönderilecek bir yerel dosya seçin.", "Uyarı");
            return;
        }

        if (_selectedRemoteNode == null)
        {
            MessageBox.Show("Önce karşı tarafta dosyayı yollayacağınız klasörü seçin.", "Uyarı");
            return;
        }

        var remoteDir = _selectedRemoteNode.IsDirectory ? _selectedRemoteNode.Path : Path.GetDirectoryName(_selectedRemoteNode.Path);

        await _fileManager.SendFileAsync(_selectedLocalNode.Path, remoteDir ?? "");
    }

    private void BtnReceive_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRemoteNode == null || _selectedRemoteNode.IsDirectory)
        {
            MessageBox.Show("Lütfen alınacak bir uzak dosya seçin.", "Uyarı");
            return;
        }

        if (_selectedLocalNode == null)
        {
            MessageBox.Show("Önce bu dosyayı yerelde kaydedeceğiniz klasörü seçin.", "Uyarı");
            return;
        }

        var localDir = _selectedLocalNode.IsDirectory ? _selectedLocalNode.Path : Path.GetDirectoryName(_selectedLocalNode.Path);

        _fsManager.RequestRemoteFile(_selectedRemoteNode.Path, localDir ?? "");
    }
}

public class TreeNodeViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public event Action<TreeNodeViewModel>? OnExpandRequested;

    public void AddDummy()
    {
        Children.Clear();
        Children.Add(new TreeNodeViewModel { Name = "..." });
    }

    public bool HasDummy => Children.Count == 1 && Children[0].Name == "...";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();

                if (_isExpanded && HasDummy)
                {
                    OnExpandRequested?.Invoke(this);
                }
            }
        }
    }

    public string SizeDisplay => IsDirectory ? "" : FormatSize(Size);

    private string FormatSize(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suf = { "B", "KB", "MB", "GB", "TB" };
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return $"{num} {suf[place]}";
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
