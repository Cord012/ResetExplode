using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Desinstalar: programas (Win32 e Store), desinstalador oficial, lote e sobras.</summary>
public partial class MainWindow
{
    private List<InstalledApp> _installedApps = new();
    private List<InstalledApp> _leftApps = new();
    private List<Leftover> _leftovers = new();
    private string _uninstSort = "nome";
    private bool _uninstBusy;
    private bool _uninstLoading;
    private bool _uninstSelectMode;
    private string _uninstSignature = "";
    private DispatcherTimer? _uninstWatch;
    private CancellationTokenSource? _uninstWait;   // pula o programa atual
    private CancellationTokenSource? _uninstBatch;  // para o lote inteiro

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
        if (_installedApps.Count == 0) UninstSummary.Text = "LENDO O REGISTRO E OS APPS DA STORE…";
        try
        {
            var selected = _installedApps.Where(a => a.Selected).Select(a => a.RegPath).ToHashSet();
            var (apps, sig) = await Task.Run(() => (Uninstaller.List(), Uninstaller.Signature()));
            foreach (var a in apps) a.Selected = selected.Contains(a.RegPath); // recarregar não perde a seleção
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
            "store" => list.OrderByDescending(a => a.IsStore).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var shown = list.ToList();
        UninstList.ItemsSource = shown;
        AppIcons.LoadAsync(shown);

        long total = shown.Sum(a => a.SizeKB) * 1024;
        int hidden = _installedApps.Count(a => !a.Visible);
        int store = shown.Count(a => a.IsStore);
        UninstSummary.Text = $"{shown.Count} {(shown.Count == 1 ? "PROGRAMA" : "PROGRAMAS")}"
                           + (store > 0 ? $" ({store} DA STORE)" : "")
                           + $" · {Uninstaller.HumanSize(total)} DECLARADOS"
                           + (!all && hidden > 0 ? $" · {hidden} DO SISTEMA OCULTOS" : "");
        UpdateBatchBar();
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

    // ---------------- Seleção (desinstalar vários) ----------------

    private void BtnUninstSelectMode_Click(object sender, RoutedEventArgs e)
    {
        _uninstSelectMode = !_uninstSelectMode;
        if (!_uninstSelectMode) foreach (var a in _installedApps) a.Selected = false;
        UninstList.Tag = _uninstSelectMode; // o modelo da linha mostra o interruptor quando Tag = true
        BtnUninstSelectMode.Content = _uninstSelectMode ? "CANCELAR SELEÇÃO" : "SELECIONAR VÁRIOS";
        UpdateBatchBar();
    }

    private void UninstSelect_Click(object sender, RoutedEventArgs e) => UpdateBatchBar();

    private void UpdateBatchBar()
    {
        if (UninstBatchBar == null) return;
        int n = _installedApps.Count(a => a.Selected);
        UninstBatchBar.Visibility = _uninstSelectMode ? Visibility.Visible : Visibility.Collapsed;
        UninstBatchText.Text = n == 0 ? "Marque os programas que quer desinstalar."
                                      : $"{n} {(n == 1 ? "programa selecionado" : "programas selecionados")}";
        BtnUninstBatch.IsEnabled = n > 0 && !_uninstBusy;
    }

    private async void BtnUninstBatch_Click(object sender, RoutedEventArgs e)
    {
        var sel = _installedApps.Where(a => a.Selected && a.CanUninstall).ToList();
        if (sel.Count == 0 || _uninstBusy) return;
        bool quiet = UninstBatchQuiet.IsChecked == true;
        int noQuiet = sel.Count(a => !a.CanQuiet);
        string names = string.Join("\n", sel.Take(15).Select(a => $"  • {a.Name}")) + (sel.Count > 15 ? $"\n  … e mais {sel.Count - 15}" : "");
        string msg = $"Desinstalar {sel.Count} programas, um de cada vez?\n\n{names}\n\n"
                   + "Antes de cada um, a chave do registro é salva em .reg."
                   + (quiet ? $"\nModo silencioso: {sel.Count - noQuiet} sem janelas" + (noQuiet > 0 ? $", {noQuiet} vão abrir o desinstalador normal (não têm modo silencioso)." : ".") : "");
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _uninstBusy = true;
        _uninstBatch = new CancellationTokenSource();
        BtnUninstStopAll.Visibility = Visibility.Visible;
        BtnUninstStopWait.Content = "PULAR ESTE";
        UpdateBatchBar();
        var removed = new List<InstalledApp>();
        var backups = new List<string>();
        var failed = new List<string>();
        for (int i = 0; i < sel.Count && !_uninstBatch.IsCancellationRequested; i++)
        {
            var (gone, backup, error) = await UninstallOne(sel[i], quiet, $"Desinstalando {i + 1} de {sel.Count}: {sel[i].Name}…");
            if (gone)
            {
                removed.Add(sel[i]);
                if (backup != null) backups.Add(backup);
                _installedApps.Remove(sel[i]);
                FillInstalledApps();
            }
            else failed.Add(sel[i].Name + (error != null ? $" ({error})" : ""));
        }
        BtnUninstStopAll.Visibility = Visibility.Collapsed;
        BtnUninstStopWait.Content = "PARAR DE ESPERAR";
        UninstWaitCard.Visibility = Visibility.Collapsed;
        _uninstBusy = false;

        UninstResult.Text = $"{removed.Count} de {sel.Count} desinstalados."
                          + (failed.Count > 0 ? $" Não concluídos: {string.Join(", ", failed)}." : "");
        if (removed.Count > 0)
        {
            Toast("Programas desinstalados", $"{removed.Count} de {sel.Count}", "");
            if (_uninstSelectMode) BtnUninstSelectMode_Click(this, new RoutedEventArgs());
            _ = LoadInstalledApps();
            await ShowLeftovers(removed, removed: true, backups);
        }
        else UpdateBatchBar();
    }

    private void BtnUninstStopAll_Click(object sender, RoutedEventArgs e)
    {
        _uninstBatch?.Cancel();
        _uninstWait?.Cancel();
    }

    // ---------------- Desinstalar (um) ----------------

    private async void UninstRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledApp app } || _uninstBusy) return;

        string msg = app.IsStore
            ? $"Desinstalar {app.Name}?\n\nÉ um app da Microsoft Store: ele sai da sua conta e dá para instalar de novo pela Store quando quiser.\n"
              + "Depois o RESET EXPLODE procura o que sobrou."
            : $"Desinstalar {app.Name}?\n\n"
              + "1. A chave do registro é salva num arquivo .reg (dá para desfazer).\n"
              + "2. O desinstalador oficial do programa abre. Siga por ele.\n"
              + "3. Depois o RESET EXPLODE procura o que sobrou. Nada é apagado sem você ver.";
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _uninstBusy = true;
        var (gone, backup, error) = await UninstallOne(app, quiet: false, $"Desinstalando {app.Name}…");
        UninstWaitCard.Visibility = Visibility.Collapsed;
        _uninstBusy = false;

        if (!gone)
        {
            UninstResult.Text = error != null
                ? $"{app.Name}: {error}"
                : $"{app.Name}: desinstalação não concluída. Se ela terminar depois, a lista se atualiza sozinha."
                  + (backup != null ? $" Backup da chave: {backup}" : "");
            UpdateBatchBar();
            return;
        }

        // Tira da tela na hora; depois relê tudo em segundo plano.
        _installedApps.RemoveAll(a => a.RegPath == app.RegPath);
        FillInstalledApps();
        UninstResult.Text = $"{app.Name} desinstalado.";
        Toast("Programa desinstalado", app.Name, "");
        _ = LoadInstalledApps();
        await ShowLeftovers(new List<InstalledApp> { app }, removed: true, backup != null ? new List<string> { backup } : new());
    }

    /// <summary>Backup (Win32) → desinstalador → espera a entrada sumir. Mostra o cartão de espera.</summary>
    private async Task<(bool Gone, string? Backup, string? Error)> UninstallOne(InstalledApp app, bool quiet, string title)
    {
        string? backup = null;
        if (!app.IsStore)
        {
            try { backup = Uninstaller.BackupKey(app.RegPath, app.Name); }
            catch (Exception ex) { return (false, null, ex.Message + ". Nada foi alterado"); }
        }

        _uninstWait = new CancellationTokenSource();
        UninstWaitCard.Visibility = Visibility.Visible;
        UninstWaitTitle.Text = title;
        UninstWaitText.Text = app.IsStore || (quiet && app.CanQuiet)
            ? "Desinstalando sem janelas. A lista se atualiza sozinha quando terminar."
            : "Termine o processo na janela do desinstalador. Quando ele acabar, o programa some daqui sozinho.";
        UninstWaitCard.BringIntoView();

        var run = Uninstaller.UninstallAsync(app, quiet);
        try { await run; }
        catch (Exception ex) { return (false, backup, "o desinstalador falhou: " + ex.Message); }

        // Muitos desinstaladores (NSIS, Inno) se copiam para o %TEMP% e o processo original fecha
        // antes de terminar. Então, em vez de confiar no "fechou", espera a entrada sumir.
        UninstWaitText.Text = "Esperando o desinstalador terminar. Se você cancelou, clique em PARAR DE ESPERAR.";
        bool gone = await Uninstaller.WaitUntilGoneAsync(app.RegPath, TimeSpan.FromMinutes(30), _uninstWait.Token);
        return (gone, backup, null);
    }

    private void BtnUninstStopWait_Click(object sender, RoutedEventArgs e) => _uninstWait?.Cancel();

    // ---------------- Sobras ----------------

    private async void UninstLeftovers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledApp app }) await ShowLeftovers(new List<InstalledApp> { app }, removed: false, new());
    }

    private async Task ShowLeftovers(List<InstalledApp> apps, bool removed, List<string> backups)
    {
        _leftApps = apps;
        string who = apps.Count == 1 ? apps[0].Name.ToUpperInvariant() : $"{apps.Count} PROGRAMAS";
        UninstLeftCard.Visibility = Visibility.Visible;
        UninstLeftTitle.Text = $"PROCURANDO SOBRAS DE {who}…";
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
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var all = new List<Leftover>();
                foreach (var app in apps)
                    foreach (var l in Uninstaller.FindLeftovers(app, installed))
                        if (seen.Add(l.Kind + "|" + l.Path)) all.Add(l);
                return all;
            });
        }
        catch (Exception ex) { UninstLeftTitle.Text = "SOBRAS"; UninstLeftInfo.Text = "Falhou: " + ex.Message; return; }

        _leftovers = found;
        UninstLeftList.ItemsSource = found;
        bool stillInstalled = apps.Any(a => Uninstaller.KeyExists(a.RegPath));
        long bytes = found.Where(l => l.CanRemove).Sum(l => l.Bytes);
        UninstLeftTitle.Text = found.Count == 0
            ? $"NENHUMA SOBRA DE {who}"
            : $"SOBRAS DE {who} · {found.Count} {(found.Count == 1 ? "ITEM" : "ITENS")} · {Uninstaller.HumanSize(bytes)}";

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
                + "Pastas vão para a Lixeira e chaves do registro são salvas em .reg antes de apagar: tudo dá para desfazer "
                + "(veja RESET FERRAMENTAS › Backups)."
                + (backups.Count == 1 ? $"\nBackup da desinstalação: {backups[0]}" : backups.Count > 1 ? $"\n{backups.Count} backups em {Uninstaller.BackupFolder}" : "");
            BtnUninstLeftRemove.IsEnabled = found.Any(l => l.CanRemove);
        }
    }

    private void BtnUninstLeftClose_Click(object sender, RoutedEventArgs e)
    {
        UninstLeftCard.Visibility = Visibility.Collapsed;
        _leftApps = new();
        _leftovers = new();
    }

    private void UninstLeftOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Leftover { IsFolder: true } l } && Directory.Exists(l.Path))
            Process.Start("explorer.exe", $"\"{l.Path}\"");
    }

    private async void BtnUninstLeftRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_leftApps.Count == 0) return;
        var sel = _leftovers.Where(l => l.Selected && l.CanRemove).ToList();
        if (sel.Count == 0) { UninstLeftResult.Text = "Nada marcado."; return; }
        int folders = sel.Count(l => l.IsFolder), keys = sel.Count - folders;
        string who = _leftApps.Count == 1 ? _leftApps[0].Name : $"{_leftApps.Count} programas";
        string msg = $"Remover {sel.Count} {(sel.Count == 1 ? "sobra" : "sobras")} de {who}?\n\n"
                   + (folders > 0 ? $"• {folders} {(folders == 1 ? "pasta vai" : "pastas vão")} para a Lixeira\n" : "")
                   + (keys > 0 ? $"• {keys} {(keys == 1 ? "chave" : "chaves")} do registro (salvas em .reg antes)\n" : "");
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        BtnUninstLeftRemove.IsEnabled = false;
        UninstLeftResult.Text = "Removendo…";
        var (ok, errors) = await Task.Run(() => Uninstaller.RemoveLeftovers(sel, who));
        string result = errors.Count == 0
            ? $"{ok} {(ok == 1 ? "item removido" : "itens removidos")}. Pastas na Lixeira; chaves com backup em RESET FERRAMENTAS › Backups."
            : $"{ok} removidos, {errors.Count} com erro: {string.Join(" | ", errors.Take(3))}";
        await ShowLeftovers(_leftApps, removed: false, new()); // procura de novo: mostra o que ainda resta
        UninstLeftResult.Text = result;
    }
}
