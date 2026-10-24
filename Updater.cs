using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResetExplode;

// Atualização automática a partir das releases do GitHub.
//
// Como sabe se tem versão nova: o build do GitHub Actions grava o commit no executável
// (-p:SourceRevisionId=<sha>, que vira "0.16.0+<sha>" na versão). A release "latest" é criada
// apontando para o commit (gh release create --target <sha>). Commit diferente = versão nova.
//
// Como troca o .exe que está rodando: o Windows não deixa apagar um .exe aberto, mas deixa
// RENOMEAR. Então: baixa ResetExplode.new.exe → renomeia o atual para .old.exe → põe o novo no
// lugar → abre o novo → fecha este. Na próxima abertura, o .old.exe é apagado.
public sealed record UpdateInfo(string Commit, DateTime Published, string Notes, string DownloadUrl, long Size, string? Sha256);

public static class Updater
{
    /// <summary>De onde vêm as atualizações (dono/repositório no GitHub).</summary>
    public const string Repo = "Cord012/ResetExplode";
    public const string AssetName = "ResetExplode.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ResetExplode-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>"0.16.0+963972d…" → ("0.16.0", "963972d…"). Sem "+": build local, sem commit.</summary>
    public static (string Version, string? Commit) Current()
    {
        string info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return SplitVersion(info);
    }

    public static (string Version, string? Commit) SplitVersion(string info)
    {
        int plus = info.IndexOf('+');
        return plus < 0 ? (info, null) : (info[..plus], info[(plus + 1)..]);
    }

    public static string ShortCommit(string? sha) => string.IsNullOrEmpty(sha) ? "local" : sha[..Math.Min(7, sha.Length)];

    public static bool IsNewer(UpdateInfo latest, string? currentCommit) =>
        currentCommit == null || !latest.Commit.StartsWith(currentCommit, StringComparison.OrdinalIgnoreCase)
                                 && !currentCommit.StartsWith(latest.Commit, StringComparison.OrdinalIgnoreCase);

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/tags/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // ainda não tem release
        resp.EnsureSuccessStatusCode();
        return ParseRelease(await resp.Content.ReadAsStringAsync(ct));
    }

    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string commit = r.TryGetProperty("target_commitish", out var t) ? t.GetString() ?? "" : "";
        DateTime published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : DateTime.MinValue;
        string notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        if (!r.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != AssetName) continue;
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            // Só baixa do próprio GitHub, do repositório configurado
            if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
            string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } dgs && dgs.StartsWith("sha256:") ? dgs[7..] : null;
            return new UpdateInfo(commit, published.ToLocalTime(), notes, url, a.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    /// <summary>Baixa ao lado do .exe atual (mesma unidade: a troca é só renomear) e confere tamanho e SHA-256.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath ?? throw new IOException("não sei onde está o executável");
        string target = Path.Combine(Path.GetDirectoryName(exe)!, "ResetExplode.new.exe");

        using (var resp = await Http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(target);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress?.Report(u.Size > 0 ? (double)done / u.Size : 0);
            }
        }

        var fi = new FileInfo(target);
        if (u.Size > 0 && fi.Length != u.Size) { File.Delete(target); throw new IOException("download incompleto"); }
        if (u.Sha256 != null)
        {
            await using var f = File.OpenRead(target);
            string got = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!got.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                f.Close();
                File.Delete(target);
                throw new IOException("o arquivo baixado não confere (SHA-256 diferente); nada foi trocado");
            }
        }
        return target;
    }

    /// <summary>Troca o .exe e abre a versão nova. Se algo falhar no meio, desfaz.</summary>
    public static void SwapAndRestart(string newExe)
    {
        string exe = Environment.ProcessPath!;
        string old = Path.ChangeExtension(exe, ".old.exe");
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);                 // renomear o .exe em uso é permitido
        try { File.Move(newExe, exe); }
        catch { File.Move(old, exe); throw; } // desfaz
        Process.Start(new ProcessStartInfo(exe, "--apos-atualizar") { UseShellExecute = false });
    }

    /// <summary>Na abertura: apaga o .old.exe que sobrou da atualização anterior.</summary>
    public static void CleanupOld()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            string old = Path.ChangeExtension(exe, ".old.exe");
            string stale = Path.Combine(Path.GetDirectoryName(exe)!, "ResetExplode.new.exe");
            if (File.Exists(old)) File.Delete(old);
            if (File.Exists(stale)) File.Delete(stale);
        }
        catch { } // o processo antigo pode ainda estar fechando; fica para a próxima
    }
}
