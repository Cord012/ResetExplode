using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Desinstalar: programas instalados, desinstalador oficial e sobras.</summary>
public partial class MainWindow
{
    private List<InstalledApp> _installedApps = new();
    private InstalledApp? _leftApp;
    private List<Leftover> _leftovers = new();
    private string _uninstSort = "nome";
    private bool _uninstBusy;

    private async void InitUninstall()
    {
        if (_installedApps.Count == 0) await LoadInstalledApps();
    }

    private async Task LoadInstalledApps()
    {
        UninstSummary.Text = "LENDO O REGISTRO…";
        try { _installedApps = await Task.Run(Uninstaller.List); }
        catch (Exception ex) { UninstSummary.Text = "NÃO FOI POSSÍVEL LER"; UninstResult.Text = ex.Message; return; }
        FillInstalledApps();
    }

    private void FillInstalledApps()
    {
        if (UninstList == null) return;
        string q = UninstSearch.Text.Trim();
        bool all = UninstShowSystem.IsChecked == true;
        var list = _installedApps.Where(a => all || a.Visible)
                        .Where(a => q.Length == 0 || $"{a.Name} {a.Publisher}".Contains(q, StringComparison.CurrentCultureIgnoreCase));
        list = _uninstSort switch
        {
            "tamanho" => list.OrderByDescending(a => a.SizeKB),
            "data" => list.OrderByDescending(a => a.InstallDate),
            _ => list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var shown = list.ToList();
        UninstList.ItemsSource = shown;

        long total = shown.Sum(a => a.SizeKB) * 1024;
        int hidden = _installedApps.Count(a => !a.Visible);
        UninstSummary.Text = $"{shown.Count} {(shown.Count == 1 ? "PROGRAMA" : "PROGRAMAS")} · {Uninstaller.HumanSize(total)} DECLARADOS"
                           + (!all && hidden > 0 ? $" · {hidden} DO SISTEMA OCULTOS" : "");
    }

    private async void BtnUninstScan_Click(object sender, RoutedEventArgs e)
    {
        UninstResult.Text = "";
        await LoadInstalledApps();
    }

    private void UninstSearch_Changed(object sender, TextChangedEventArgs e) => FillInstalledApps();
    private void UninstShowSystem_Click(object sender, RoutedEventArgs e) => FillInstalledApps();

    private void UninstSort_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) _uninstSort = tag;
        FillInstalledApps();
    }

    // ---------------- Sobras ----------------

    private async void UninstLeftovers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledApp app }) await ShowLeftovers(app, removed: false, backup: null);
    }

    private async Task ShowLeftovers(InstalledApp app, bool removed, string? backup)
    {
        _leftApp = app;
        UninstLeftCard.Visibility = Visibility.Visible;
        UninstLeftTitle.Text = $"PROCURANDO SOBRAS DE {app.Name.ToUpperInvariant()}…";
        UninstLeftInfo.Text = "";
        UninstLeftResult.Text = "";
        UninstLeftList.ItemsSource = null;
        BtnUninstLeftRemove.IsEnabled = false;
        UninstLeftCard.BringIntoView();

        List<Leftover> found;
        try
        {
            found = await Task.Run(() =>
            {
                var installed = Uninstaller.List();
                return Uninstaller.FindLeftovers(app, installed);
            });
        }
        catch (Exception ex) { UninstLeftTitle.Text = "SOBRAS"; UninstLeftInfo.Text = "Falhou: " + ex.Message; return; }

        _leftovers = found;
        UninstLeftList.ItemsSource = found;
        bool stillInstalled = Uninstaller.KeyExists(app.RegPath);
        long bytes = found.Where(l => l.CanRemove).Sum(l => l.Bytes);
        UninstLeftTitle.Text = found.Count == 0
            ? $"NENHUMA SOBRA DE {app.Name.ToUpperInvariant()}"
            : $"SOBRAS DE {app.Name.ToUpperInvariant()} · {found.Count} {(found.Count == 1 ? "ITEM" : "ITENS")} · {Uninstaller.HumanSize(bytes)}";

        if (stillInstalled)
        {
            // Com o programa instalado, isso ainda é dele: remover agora quebraria o programa.
            UninstLeftInfo.Text = "O programa ainda está instalado, então isso ainda é dele. Esta lista mostra o que provavelmente "
                                + "vai ficar para trás. Para remover sobras, desinstale primeiro.";
            BtnUninstLeftRemove.IsEnabled = false;
        }
        else
        {
            UninstLeftInfo.Text = (removed ? "Desinstalado. " : "")
                + "ALTA vem marcada; MÉDIA você confere antes; NÃO MEXER é de outro programa e fica bloqueado. "
                + "Pastas vão para a Lixeira e chaves do registro são salvas em .reg antes de apagar: tudo dá para desfazer."
                + (backup != null ? $"\nBackup da desinstalação: {backup}" : "");
            BtnUninstLeftRemove.IsEnabled = found.Any(l => l.CanRemove);
        }
    }

    private void BtnUninstLeftClose_Click(object sender, RoutedEventArgs e)
    {
        UninstLeftCard.Visibility = Visibility.Collapsed;
        _leftApp = null;
        _leftovers = new();
    }

    private void UninstLeftOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Leftover { IsFolder: true } l } && Directory.Exists(l.Path))
            Process.Start("explorer.exe", $"\"{l.Path}\"");
    }

    private async void BtnUninstLeftRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_leftApp is not { } app) return;
        var sel = _leftovers.Where(l => l.Selected && l.CanRemove).ToList();
        if (sel.Count == 0) { UninstLeftResult.Text = "Nada marcado."; return; }
        int folders = sel.Count(l => l.IsFolder), keys = sel.Count - folders;
        string msg = $"Remover {sel.Count} {(sel.Count == 1 ? "sobra" : "sobras")} de {app.Name}?\n\n"
                   + (folders > 0 ? $"• {folders} {(folders == 1 ? "pasta vai" : "pastas vão")} para a Lixeira\n" : "")
                   + (keys > 0 ? $"• {keys} {(keys == 1 ? "chave" : "chaves")} do registro (salvas em .reg antes)\n" : "");
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        BtnUninstLeftRemove.IsEnabled = false;
        UninstLeftResult.Text = "Removendo…";
        var (ok, errors) = await Task.Run(() => Uninstaller.RemoveLeftovers(sel, app.Name));
        string result = errors.Count == 0
            ? $"{ok} {(ok == 1 ? "item removido" : "itens removidos")}. Pastas na Lixeira; chaves com backup em {Uninstaller.BackupFolder}"
            : $"{ok} removidos, {errors.Count} com erro: {string.Join(" | ", errors.Take(3))}";
        await ShowLeftovers(app, removed: false, backup: null); // procura de novo: mostra o que ainda resta
        UninstLeftResult.Text = result;
    }

    // ---------------- Desinstalar ----------------

    private async void UninstRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledApp app } || _uninstBusy) return;

        string msg = $"Desinstalar {app.Name}?\n\n"
                   + "1. A chave do registro é salva num arquivo .reg (dá para desfazer).\n"
                   + "2. O desinstalador oficial do programa abre. Siga por ele.\n"
                   + "3. Depois o RESET EXPLODE procura o que sobrou. Nada é apagado sem você ver.";
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        string backup;
        try { backup = Uninstaller.BackupKey(app.RegPath, app.Name); }
        catch (Exception ex) { UninstResult.Text = $"{app.Name}: {ex.Message}. Nada foi alterado."; return; }

        _uninstBusy = true;
        UninstResult.Text = $"Desinstalador de {app.Name} aberto. Termine o processo na janela dele…";
        try { await Uninstaller.RunUninstallerAsync(app.UninstallString); }
        catch (Exception ex) { UninstResult.Text = $"{app.Name}: o desinstalador não abriu ({ex.Message})."; _uninstBusy = false; return; }

        // Muitos desinstaladores (NSIS, Inno) se copiam para o %TEMP% e o processo original
        // fecha antes de terminar. Então confere antes de concluir.
        while (Uninstaller.KeyExists(app.RegPath))
        {
            var r = MessageBox.Show(
                $"{app.Name} ainda aparece como instalado.\n\nSe a janela do desinstalador ainda está aberta, termine nela e clique OK "
                + "para verificar de novo. Se você cancelou a desinstalação, clique Cancelar.",
                "RESET EXPLODE", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (r != MessageBoxResult.OK)
            {
                _uninstBusy = false;
                UninstResult.Text = $"{app.Name}: desinstalação não concluída. Backup da chave: {backup}";
                return;
            }
        }

        _uninstBusy = false;
        UninstResult.Text = $"{app.Name} desinstalado.";
        await LoadInstalledApps();
        await ShowLeftovers(app, removed: true, backup: backup);
    }
}
