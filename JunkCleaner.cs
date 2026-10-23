namespace ResetExplode;

// RESET LIMPEZA: arquivos temporários e caches que o Windows e os programas recriam sozinhos.
//
// Regras de segurança:
//  - Só pastas conhecidas, nunca "procurar lixo" pelo disco todo.
//  - Não segue atalhos de pasta (junction/symlink): não dá para "escapar" da pasta.
//  - Arquivo em uso é pulado, sem erro.
//  - Temporários só entram com mais de 24 h (instalação em andamento não é afetada).
//  - Prefetch NÃO entra: apagar faz o Windows abrir os programas mais devagar até reconstruir.
//    (É o "mito da limpeza" mais comum.)

public sealed class JunkSource
{
    public string Dir { get; init; } = "";
    public string Pattern { get; init; } = "*";
    public bool Recursive { get; init; } = true;
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
}

public sealed class JunkCategory
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool NeedsAdmin { get; init; }
    public bool DefaultOn { get; init; } = true;
    public bool IsRecycleBin { get; init; }
    public Func<IEnumerable<JunkSource>> Sources { get; init; } = () => Array.Empty<JunkSource>();

    // Resultado da análise
    public long Bytes { get; set; }
    public long Files { get; set; }
    public bool Selected { get; set; }
    public bool Available { get; set; } = true;

    public string SizeText => Bytes > 0 ? Uninstaller.HumanSize(Bytes) : "vazio";
    public string CountText => IsRecycleBin ? $"{Files} {(Files == 1 ? "item" : "itens")}" : $"{Files} {(Files == 1 ? "arquivo" : "arquivos")}";
    public string AdminText => NeedsAdmin ? "ADMIN" : "";
}

public sealed record JunkCleanResult(long FreedBytes, long Deleted, long Skipped);

public static class JunkCleaner
{
    private static string E(string name) => Environment.GetEnvironmentVariable(name) ?? "";
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string ProgData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public static List<JunkCategory> Categories() => new()
    {
        new()
        {
            Id = "usertemp", Name = "Temporários do usuário",
            Description = "Sobras de programas e instaladores na sua pasta %TEMP%. Só entra o que tem mais de 24 h.",
            Sources = () => new[] { new JunkSource { Dir = Path.GetTempPath(), MinAge = Day } },
        },
        new()
        {
            Id = "wintemp", Name = "Temporários do Windows", NeedsAdmin = true,
            Description = "A pasta Temp do próprio Windows (C:\\Windows\\Temp). Só entra o que tem mais de 24 h.",
            Sources = () => new[] { new JunkSource { Dir = Path.Combine(Win, "Temp"), MinAge = Day } },
        },
        new()
        {
            Id = "recycle", Name = "Lixeira", IsRecycleBin = true,
            Description = "Esvazia a Lixeira de todas as unidades. Depois disso não dá para restaurar.",
        },
        new()
        {
            Id = "thumbs", Name = "Cache de miniaturas",
            Description = "Miniaturas de fotos e vídeos que o Explorer guarda. Ele recria quando você abrir as pastas.",
            Sources = () => new[]
            {
                new JunkSource { Dir = Path.Combine(Local, @"Microsoft\Windows\Explorer"), Pattern = "thumbcache_*.db", Recursive = false },
                new JunkSource { Dir = Path.Combine(Local, @"Microsoft\Windows\Explorer"), Pattern = "iconcache_*.db", Recursive = false },
            },
        },
        new()
        {
            Id = "wer", Name = "Relatórios de erro",
            Description = "Relatórios que o Windows junta quando um programa trava, para mandar à Microsoft.",
            Sources = () => new[]
            {
                new JunkSource { Dir = Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive") },
                new JunkSource { Dir = Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue") },
                new JunkSource { Dir = Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive") },
                new JunkSource { Dir = Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue") },
            },
        },
        new()
        {
            Id = "dumps", Name = "Dumps de travamento", DefaultOn = false,
            Description = "Cópias da memória salvas em travamentos e telas azuis. Só servem se você for investigar um travamento.",
            Sources = () => new[]
            {
                new JunkSource { Dir = Path.Combine(Local, "CrashDumps") },
                new JunkSource { Dir = Path.Combine(Win, "Minidump") },
                new JunkSource { Dir = Win, Pattern = "MEMORY.DMP", Recursive = false },
            },
        },
        new()
        {
            Id = "browsers", Name = "Cache de navegadores",
            Description = "Só o cache (imagens e scripts baixados) do Chrome, Edge, Brave e Firefox. Senhas, histórico e logins não são tocados. Feche o navegador antes.",
            Sources = BrowserCaches,
        },
        new()
        {
            Id = "wu", Name = "Downloads do Windows Update", NeedsAdmin = true, DefaultOn = false,
            Description = "Instaladores de atualizações já baixadas (com mais de 7 dias). O Windows baixa de novo se precisar.",
            Sources = () => new[] { new JunkSource { Dir = Path.Combine(Win, @"SoftwareDistribution\Download"), MinAge = TimeSpan.FromDays(7) } },
        },
    };

    /// <summary>Pastas de cache dos navegadores baseados em Chromium (todos os perfis) e do Firefox.</summary>
    private static IEnumerable<JunkSource> BrowserCaches()
    {
        foreach (var userData in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data" })
        {
            string root = Path.Combine(Local, userData);
            foreach (var profile in SafeDirs(root))
                foreach (var cache in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" })
                {
                    string dir = Path.Combine(root, profile, cache);
                    if (Directory.Exists(dir)) yield return new JunkSource { Dir = dir };
                }
        }
        string ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        foreach (var profile in SafeDirs(ff))
        {
            string dir = Path.Combine(ff, profile, "cache2");
            if (Directory.Exists(dir)) yield return new JunkSource { Dir = dir };
        }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? new DirectoryInfo(path).EnumerateDirectories().Select(d => d.Name).ToList() : new(); }
        catch { return Array.Empty<string>(); }
    }

    private static readonly EnumerationOptions Deep = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // não segue junction/symlink
    };
    private static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Os arquivos que entram na limpeza desta origem.</summary>
    public static IEnumerable<FileInfo> Files(JunkSource src)
    {
        if (src.Dir.Length < 4 || !Directory.Exists(src.Dir)) yield break; // nunca a raiz de um disco
        DateTime limit = DateTime.UtcNow - src.MinAge;
        IEnumerable<FileInfo> all;
        try { all = new DirectoryInfo(src.Dir).EnumerateFiles(src.Pattern, src.Recursive ? Deep : Shallow); }
        catch { yield break; }
        using var it = all.GetEnumerator();
        while (true)
        {
            FileInfo f;
            try { if (!it.MoveNext()) yield break; f = it.Current; }
            catch { yield break; }
            if (f.LastWriteTimeUtc <= limit) yield return f;
        }
    }

    /// <summary>Mede cada categoria (não apaga nada).</summary>
    public static void Analyze(IEnumerable<JunkCategory> cats, bool isAdmin)
    {
        foreach (var c in cats)
        {
            c.Available = !c.NeedsAdmin || isAdmin;
            if (c.IsRecycleBin)
            {
                (c.Bytes, c.Files) = RecycleBin.Query();
            }
            else
            {
                long bytes = 0, n = 0;
                foreach (var src in c.Sources())
                    foreach (var f in Files(src))
                    {
                        try { bytes += f.Length; n++; } catch { }
                    }
                (c.Bytes, c.Files) = (bytes, n);
            }
            c.Selected = c.Available && c.DefaultOn && c.Bytes > 0;
        }
    }

    /// <summary>Apaga os arquivos das categorias marcadas. Arquivo em uso é pulado.</summary>
    public static JunkCleanResult Clean(IEnumerable<JunkCategory> cats, IProgress<string>? progress = null)
    {
        long freed = 0, deleted = 0, skipped = 0;
        foreach (var c in cats.Where(c => c.Selected && c.Available))
        {
            progress?.Report(c.Name);
            if (c.IsRecycleBin)
            {
                var (bytes, items) = RecycleBin.Query();
                RecycleBin.Empty();
                var (after, _) = RecycleBin.Query();
                freed += Math.Max(0, bytes - after);
                deleted += items;
                continue;
            }
            foreach (var src in c.Sources())
            {
                foreach (var f in Files(src).ToList())
                {
                    try
                    {
                        long len = f.Length;
                        if (f.IsReadOnly) f.IsReadOnly = false;
                        f.Delete();
                        freed += len;
                        deleted++;
                    }
                    catch { skipped++; } // em uso ou sem permissão
                }
                if (src.Recursive) RemoveEmptyDirs(src.Dir);
            }
        }
        return new JunkCleanResult(freed, deleted, skipped);
    }

    /// <summary>Apaga subpastas que ficaram vazias (de baixo pra cima). A pasta raiz fica.</summary>
    private static void RemoveEmptyDirs(string root)
    {
        try
        {
            var dirs = new DirectoryInfo(root).EnumerateDirectories("*", Deep)
                .Select(d => d.FullName).OrderByDescending(p => p.Length).ToList();
            foreach (var d in dirs)
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch { }
            }
        }
        catch { }
    }
}
