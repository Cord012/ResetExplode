using System.Windows;

namespace ResetExplode;

/// <summary>Atualização: versão no rodapé do menu, busca automática ao abrir e botão na barra de título.</summary>
public partial class MainWindow
{
    private bool _updating;

    private async void InitUpdater()
    {
        var (ver, commit) = Updater.Current();
        SideVersion.Text = $"v{ver} · {Updater.ShortCommit(commit)}";
        Updater.CleanupOld();
        try
        {
            var u = await Updater.CheckAsync();
            if (u != null && commit != null && Updater.IsNewer(u, commit))
            {
                BtnUpdateAvailable.Visibility = Visibility.Visible;
                BtnUpdateAvailable.ToolTip = $"Versão nova de {u.Published:dd/MM HH:mm}";
            }
        }
        catch { } // sem internet ou GitHub fora: não incomoda ninguém ao abrir
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var (ver, commit) = Updater.Current();

        UpdateInfo? u;
        BtnUpdateCheck.IsEnabled = false;
        try { u = await Updater.CheckAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Não consegui consultar o GitHub: {ex.Message}", "RESET EXPLODE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { BtnUpdateCheck.IsEnabled = true; }

        if (u == null)
        {
            MessageBox.Show($"Ainda não tem versão publicada em github.com/{Updater.Repo}/releases.", "RESET EXPLODE",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (commit != null && !Updater.IsNewer(u, commit))
        {
            BtnUpdateAvailable.Visibility = Visibility.Collapsed;
            MessageBox.Show($"Você já está na versão mais recente (v{ver} · {Updater.ShortCommit(commit)}).", "RESET EXPLODE",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string notes = string.Join("\n", u.Notes.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(10));
        if (notes.Length > 700) notes = notes[..700] + "…";
        string msg = (commit == null ? "Esta cópia foi compilada na mão, então não dá para comparar as versões.\n\n" : "")
                   + $"Versão nova publicada em {u.Published:dd/MM/yyyy HH:mm} ({Updater.ShortCommit(u.Commit)}), {Uninstaller.HumanSize(u.Size)}.\n\n"
                   + (notes.Length > 0 ? notes + "\n\n" : "")
                   + "Baixar e atualizar agora? O RESET EXPLODE fecha e abre de novo sozinho.";
        if (MessageBox.Show(msg, "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _updating = true;
        string title = TitlePage.Text;
        var progress = new Progress<double>(p => TitlePage.Text = $"Baixando atualização… {p:P0}");
        try
        {
            string file = await Updater.DownloadAsync(u, progress);
            TitlePage.Text = "Instalando atualização…";
            Updater.SwapAndRestart(file);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            TitlePage.Text = title;
            _updating = false;
            MessageBox.Show($"A atualização falhou: {ex.Message}\n\nNada foi trocado; você continua na versão atual.", "RESET EXPLODE",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
