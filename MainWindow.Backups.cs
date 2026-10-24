using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Backups: os .reg salvos pelo Desinstalar, com restaurar.</summary>
public partial class MainWindow
{
    private void ScanBackups()
    {
        List<RegBackup> list;
        try { list = Backups.List(); }
        catch (Exception ex) { BackupsSummary.Text = "NÃO FOI POSSÍVEL LER"; BackupsResult.Text = ex.Message; return; }
        BackupsList.ItemsSource = list;
        BackupsSummary.Text = list.Count == 0
            ? "NENHUM BACKUP AINDA · ELES APARECEM AQUI DEPOIS QUE VOCÊ DESINSTALA OU REMOVE SOBRAS"
            : $"{list.Count} {(list.Count == 1 ? "BACKUP" : "BACKUPS")} · {list.Count(b => b.IsLeftover)} DE SOBRAS · {list.Count(b => !b.IsLeftover)} DE DESINSTALAÇÕES";
    }

    private void BtnBackupsScan_Click(object sender, RoutedEventArgs e)
    {
        BackupsResult.Text = "";
        ScanBackups();
    }

    private void BtnBackupsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Uninstaller.BackupFolder);
        Process.Start("explorer.exe", $"\"{Uninstaller.BackupFolder}\"");
    }

    private void BackupRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RegBackup b }) return;
        string msg = $"Restaurar o backup de {b.WhenText}?\n\n{b.Key}\n\n{b.Hint}";
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            Backups.Restore(b);
            BackupsResult.Text = $"Restaurado: {b.Key}";
            _uninstSignature = ""; // a lista do Desinstalar relê na próxima vez
        }
        catch (Exception ex) { BackupsResult.Text = $"Falhou: {ex.Message}"; }
    }

    private void BackupDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RegBackup b }) return;
        if (MessageBox.Show($"Mandar este backup para a Lixeira?\n\n{b.Label} ({b.WhenText})", "RESET EXPLODE",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try { RecycleBin.Send(b.File); BackupsResult.Text = "Backup na Lixeira."; }
        catch (Exception ex) { BackupsResult.Text = $"Falhou: {ex.Message}"; }
        ScanBackups();
    }
}
