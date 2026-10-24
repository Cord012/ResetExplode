using System.IO;

namespace ResetExplode;

// Arquivos grandes: o que mais ocupa espaço numa pasta ou unidade.
// Mostra as subpastas da pasta escolhida com o tamanho total de cada uma (dá para "entrar" nelas)
// e os maiores arquivos de tudo o que está dentro. Não segue junction/symlink (não conta duas vezes
// e não sai da pasta escolhida).

public sealed class BigEntry
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Bytes { get; set; }
    public long Files { get; set; }
    public bool IsFolder { get; init; }
    public double Share { get; set; }        // fração do total (barrinha)
    public string Note { get; set; } = "";   // explicação de arquivos conhecidos do Windows
    public bool Protected { get; set; }      // do Windows / de programa: use Desinstalar, não a Lixeira

    public string SizeText => Uninstaller.HumanSize(Bytes);
    public string Detail => IsFolder
        ? $"{Files:N0} arquivos" + (Note.Length > 0 ? "  ·  " + Note : "")
        : (Note.Length > 0 ? Note : System.IO.Path.GetDirectoryName(Path) ?? "");
    public bool CanRecycle => !Protected;
    public double BarWidth => Math.Max(2, Share * 120);
}

public sealed record BigScanResult(string Root, long TotalBytes, long TotalFiles, List<BigEntry> Folders, List<BigEntry> Files, int Inaccessible);

public static class BigFiles
{
    /// <summary>Arquivos enormes que o Windows cria e que NÃO se apagam na mão.</summary>
    private static readonly Dictionary<string, string> KnownSystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pagefile.sys"] = "memória virtual do Windows (ajuste em Sistema › Avançado › Desempenho)",
        ["swapfile.sys"] = "memória virtual dos apps da Store, gerenciada pelo Windows",
        ["hiberfil.sys"] = "hibernação; dá para desligar com \"powercfg /h off\" no terminal como administrador",
        ["MEMORY.DMP"] = "dump de tela azul; pode ir pela Limpeza (Dumps de travamento)",
    };

    public static bool IsProtectedPath(string path)
    {
        string p = path.TrimEnd('\\');
        foreach (var sys in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramData") ?? "",
        })
        {
            if (sys.Length > 3 && (p.Equals(sys, StringComparison.OrdinalIgnoreCase) || p.StartsWith(sys + "\\", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return KnownSystemFiles.ContainsKey(System.IO.Path.GetFileName(p)) || System.IO.Path.GetPathRoot(p)?.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static readonly EnumerationOptions Opts = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
    };

    /// <summary>Varre <paramref name="root"/>. progress recebe a contagem de arquivos de tempos em tempos.</summary>
    public static BigScanResult Scan(string root, int topFiles, IProgress<long>? progress, CancellationToken ct)
    {
        var folders = new Dictionary<string, BigEntry>(StringComparer.OrdinalIgnoreCase);
        var heap = new PriorityQueue<BigEntry, long>(); // os N maiores: tira o menor quando passa de N
        long total = 0, files = 0;
        int inaccessible = 0;
        var lastReport = DateTime.UtcNow;

        void AddFile(FileInfo f, BigEntry? bucket)
        {
            long len;
            try { len = f.Length; } catch { return; }
            total += len; files++;
            if (bucket != null) { bucket.Bytes += len; bucket.Files++; }
            heap.Enqueue(new BigEntry { Path = f.FullName, Name = f.Name, Bytes = len }, len);
            if (heap.Count > topFiles) heap.Dequeue();
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
            {
                progress.Report(files);
                lastReport = DateTime.UtcNow;
            }
        }

        var rootInfo = new DirectoryInfo(root);
        // Arquivos soltos na raiz escolhida
        foreach (var f in SafeFiles(rootInfo, ref inaccessible)) AddFile(f, null);

        // Cada subpasta direta vira um "balde"; tudo o que está dentro soma nele
        foreach (var dir in SafeDirs(rootInfo, ref inaccessible))
        {
            ct.ThrowIfCancellationRequested();
            var bucket = new BigEntry { Path = dir.FullName, Name = dir.Name, IsFolder = true };
            folders[dir.FullName] = bucket;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                foreach (var f in SafeFiles(d, ref inaccessible)) AddFile(f, bucket);
                foreach (var sub in SafeDirs(d, ref inaccessible)) stack.Push(sub);
            }
        }

        var folderList = folders.Values.Where(f => f.Bytes > 0).OrderByDescending(f => f.Bytes).ToList();
        var fileList = new List<BigEntry>();
        while (heap.Count > 0) fileList.Add(heap.Dequeue());
        fileList.Reverse();

        long maxF = Math.Max(1, folderList.FirstOrDefault()?.Bytes ?? 1), maxA = Math.Max(1, fileList.FirstOrDefault()?.Bytes ?? 1);
        foreach (var e in folderList) { e.Share = (double)e.Bytes / maxF; e.Protected = IsProtectedPath(e.Path); }
        foreach (var e in fileList)
        {
            e.Share = (double)e.Bytes / maxA;
            e.Protected = IsProtectedPath(e.Path);
            if (KnownSystemFiles.TryGetValue(e.Name, out var note)) e.Note = note;
        }
        progress?.Report(files);
        return new BigScanResult(root, total, files, folderList, fileList, inaccessible);
    }

    private static IEnumerable<FileInfo> SafeFiles(DirectoryInfo d, ref int inaccessible)
    {
        try { return d.EnumerateFiles("*", Opts).ToList(); }
        catch { inaccessible++; return Array.Empty<FileInfo>(); }
    }

    private static IEnumerable<DirectoryInfo> SafeDirs(DirectoryInfo d, ref int inaccessible)
    {
        try { return d.EnumerateDirectories("*", Opts).ToList(); }
        catch { inaccessible++; return Array.Empty<DirectoryInfo>(); }
    }
}
