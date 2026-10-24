using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Desinstalar: programas instalados, desinstalador oficial e sobras.</summary>
public partial class MainWindow
{
    private List<InstalledApp> _installedApps = new();
    private InstalledApp? _leftApp;
    private List<Leftover> _leftovers = new();
    private string _uninstSort = "nome";
    private bool _uninstBusy;
    private bool _uninstLoading;
    private string _uninstSignature = "";
    private DispatcherTimer? _uninstWatch;
    private CancellationTokenSource? _uninstWait;

    private async void InitUninstall()
    {
        StartUninstallWatch();
        if (_installedApps.Count == 0) await LoadInstalledApps();
    }

    /// <summary>
    /// Enquanto a aba está aberta, confere a cada 3 s se a lista de instalados mudou
    /// (instalou/desinstalou algo, até pelas Configurações do Windows) e recarrega sozinho.
    /// </summary>
    private void StartUninstallWatch()
    {
        if (_uninstWatch != null) return;
        _uninstWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _uninstWatch.Tick += async (_, _) =>
        {
            if (!PageUninstall.IsVisible || _uninstLoading || _uninstBusy) return;
            string sig = await Task.Run(Uninstaller.Signature);
            if (sig != _uninstSignature) await LoadInstalledApps();
        };
        _uninstWatch.Start();
    }

    private async Task LoadInstalledApps()
    {
        if (_uninstLoading) return;
        _uninstLoading = true;
        if (_installedApps.Count == 0) UninstSummary.Text = "LENDO O REGISTRO…";
        try
        {
            var (apps, sig) = await Task.Run(() => (Uninstaller.List(), Uninstaller.Signature()));
            _installedApps = apps;
            _uninstSignature = sig;
        }
        catch (Exception ex) { UninstSummary.Text = "NÃO FOI POSSÍVEL LER"; UninstResult.Text = ex.Message; return; }
        finally { _uninstLoading = false; }
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
        _uninstWait = new CancellationTokenSource();
        UninstWaitCard.Visibility = Visibility.Visible;
        UninstWaitTitle.Text = $"Desinstalando {app.Name}…";
        UninstWaitText.Text = "Termine o processo na janela do desinstalador. Quando ele acabar, o programa some daqui sozinho.";
        UninstWaitCard.BringIntoView();

        try { await Uninstaller.RunUninstallerAsync(app.UninstallString); }
        catch (Exception ex)
        {
            UninstWaitCard.Visibility = Visibility.Collapsed;
            UninstResult.Text = $"{app.Name}: o desinstalador não abriu ({ex.Message}).";
            _uninstBusy = false;
            return;
        }

        // Muitos desinstaladores (NSIS, Inno) se copiam para o %TEMP% e o processo original fecha
        // antes de terminar. Então, em vez de confiar no "fechou", espera a chave sumir do registro.
        UninstWaitText.Text = "Esperando o desinstalador terminar. Se você cancelou a desinstalação, clique em PARAR DE ESPERAR.";
        bool gone = await Uninstaller.WaitUntilGoneAsync(app.RegPath, TimeSpan.FromMinutes(30), _uninstWait.Token);
        UninstWaitCard.Visibility = Visibility.Collapsed;
        _uninstBusy = false;

        if (!gone)
        {
            UninstResult.Text = $"{app.Name}: desinstalação não concluída. Se ela terminar depois, a lista se atualiza sozinha. Backup da chave: {backup}";
            return;
        }

        // Tira da tela na hora; depois relê o registro inteiro em segundo plano.
        _installedApps.RemoveAll(a => a.RegPath == app.RegPath);
        FillInstalledApps();
        UninstResult.Text = $"{app.Name} desinstalado.";
        Toast("Programa desinstalado", app.Name, "");
        _ = LoadInstalledApps();
        await ShowLeftovers(app, removed: true, backup: backup);
    }

    private void BtnUninstStopWait_Click(object sender, RoutedEventArgs e) => _uninstWait?.Cancel();
}
