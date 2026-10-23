using System.Windows;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Limpeza: temporários e caches.</summary>
public partial class MainWindow
{
    private List<JunkCategory> _junk = new();
    private bool _cleanBusy;

    private async void InitClean()
    {
        if (_junk.Count == 0 && !_cleanBusy) await AnalyzeJunk();
    }

    private async Task AnalyzeJunk()
    {
        _cleanBusy = true;
        BtnCleanAnalyze.IsEnabled = BtnClean.IsEnabled = false;
        CleanTotal.Text = "…";
        CleanSummary.Text = "Medindo as pastas. Nada é apagado na análise.";
        var cats = JunkCleaner.Categories();
        try { await Task.Run(() => JunkCleaner.Analyze(cats, SensorMonitor.IsElevated)); }
        catch (Exception ex) { CleanSummary.Text = "Falhou: " + ex.Message; }
        _junk = cats;
        CleanList.ItemsSource = null;
        CleanList.ItemsSource = _junk;
        UpdateCleanTotal();
        _cleanBusy = false;
        BtnCleanAnalyze.IsEnabled = true;
    }

    private void UpdateCleanTotal()
    {
        var sel = _junk.Where(c => c.Selected && c.Available).ToList();
        long bytes = sel.Sum(c => c.Bytes);
        CleanTotal.Text = Uninstaller.HumanSize(bytes);
        CleanSummary.Text = sel.Count == 0
            ? "Nada marcado. Ligue as categorias que quer limpar."
            : $"{sel.Count} {(sel.Count == 1 ? "categoria marcada" : "categorias marcadas")} · {sel.Sum(c => c.Files)} arquivos";
        BtnClean.IsEnabled = !_cleanBusy && bytes > 0;
    }

    private async void BtnCleanAnalyze_Click(object sender, RoutedEventArgs e)
    {
        CleanResult.Text = "";
        await AnalyzeJunk();
    }

    private void CleanToggle_Click(object sender, RoutedEventArgs e) => UpdateCleanTotal();

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        var sel = _junk.Where(c => c.Selected && c.Available).ToList();
        if (sel.Count == 0) return;
        string list = string.Join("\n", sel.Select(c => $"  • {c.Name} ({c.SizeText})"));
        bool recycle = sel.Any(c => c.IsRecycleBin);
        string msg = $"Limpar {Uninstaller.HumanSize(sel.Sum(c => c.Bytes))}?\n\n{list}\n\n"
                   + "Esses arquivos são recriados sozinhos quando precisarem."
                   + (recycle ? "\nA Lixeira será esvaziada: o que está nela não poderá ser restaurado." : "");
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _cleanBusy = true;
        BtnClean.IsEnabled = BtnCleanAnalyze.IsEnabled = false;
        var progress = new Progress<string>(name => CleanResult.Text = $"Limpando: {name}…");
        JunkCleanResult r;
        try { r = await Task.Run(() => JunkCleaner.Clean(sel, progress)); }
        catch (Exception ex) { CleanResult.Text = "Falhou: " + ex.Message; _cleanBusy = false; BtnCleanAnalyze.IsEnabled = true; return; }
        _cleanBusy = false;

        await AnalyzeJunk();
        CleanResult.Text = $"Liberado {Uninstaller.HumanSize(r.FreedBytes)} · {r.Deleted} arquivos apagados"
                         + (r.Skipped > 0 ? $" · {r.Skipped} em uso ou protegidos (pulados)" : "") + ".";
    }
}
