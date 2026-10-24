using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Arquivos grandes.</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _bigScan;
    private string? _bigRoot;

    private void InitBigFiles()
    {
        if (BigDrives.Children.Count > 0) return;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            string label = $"{d.Name.TrimEnd('\\')}  {Uninstaller.HumanSize(d.AvailableFreeSpace)} livres de {Uninstaller.HumanSize(d.TotalSize)}";
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "BigDrive", Content = label, Tag = d.RootDirectory.FullName };
            chip.Checked += (_, _) => _ = ScanBig((string)chip.Tag);
            BigDrives.Children.Add(chip);
        }
    }

    private async Task ScanBig(string root)
    {
        _bigScan?.Cancel();
        var cts = _bigScan = new CancellationTokenSource();
        _bigRoot = root;
        BigPath.Text = root;
        string trimmed = root.TrimEnd('\\');
        BtnBigUp.IsEnabled = trimmed.Length > 2 && Directory.GetParent(trimmed) != null; // na raiz do disco não tem "voltar"
        BtnBigStop.Visibility = Visibility.Visible;
        BigStatus.Text = "Contando arquivos…";
        BigFolderList.ItemsSource = null;
        BigFileList.ItemsSource = null;

        var progress = new Progress<long>(n => { if (!cts.IsCancellationRequested) BigStatus.Text = $"{n:N0} arquivos contados…"; });
        try
        {
            var r = await Task.Run(() => BigFiles.Scan(root, 40, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            BigFolderList.ItemsSource = r.Folders;
            BigFileList.ItemsSource = r.Files;
            BigStatus.Text = $"{Uninstaller.HumanSize(r.TotalBytes)} em {r.TotalFiles:N0} arquivos"
                           + (r.Inaccessible > 0 ? $" · {r.Inaccessible} pastas sem acesso foram puladas" : "");
        }
        catch (OperationCanceledException) { BigStatus.Text = "Parado."; }
        catch (Exception ex) { BigStatus.Text = "Falhou: " + ex.Message; }
        finally { if (_bigScan == cts) BtnBigStop.Visibility = Visibility.Collapsed; }
    }

    private void BtnBigStop_Click(object sender, RoutedEventArgs e) => _bigScan?.Cancel();

    private void BtnBigUp_Click(object sender, RoutedEventArgs e)
    {
        if (_bigRoot == null) return;
        var parent = Directory.GetParent(_bigRoot.TrimEnd('\\'));
        if (parent != null) _ = ScanBig(parent.FullName);
    }

    private void BtnBigPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Onde procurar arquivos grandes" };
        if (dlg.ShowDialog(this) == true) _ = ScanBig(dlg.FolderName);
    }

    private void BigFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BigEntry { IsFolder: true } f }) _ = ScanBig(f.Path);
    }

    private void BigShow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BigEntry f } && File.Exists(f.Path))
            Process.Start("explorer.exe", $"/select,\"{f.Path}\"");
    }

    private void BigRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BigEntry f } || f.Protected) return;
        if (MessageBox.Show($"Mandar para a Lixeira?\n\n{f.Path}\n{f.SizeText}\n\nDá para restaurar pela Lixeira enquanto ela não for esvaziada.",
                "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            RecycleBin.Send(f.Path);
            if (BigFileList.ItemsSource is List<BigEntry> list)
            {
                list.Remove(f);
                BigFileList.ItemsSource = null;
                BigFileList.ItemsSource = list;
            }
            BigStatus.Text = $"{f.Name} foi para a Lixeira ({f.SizeText}).";
        }
        catch (Exception ex) { BigStatus.Text = "Falhou: " + ex.Message; }
    }
}
