using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResetExplode;

// RESET DESINSTALAR: lista os programas instalados, roda o desinstalador oficial
// e procura o que sobrou (pastas e chaves de registro).
//
// Onde o Windows guarda a lista de instalados (a mesma de "Aplicativos instalados"):
//   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall               (64 bits)
//   HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall   (32 bits)
//   HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall               (só este usuário)

public sealed class InstalledApp
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string InstallDate { get; init; } = ""; // AAAAMMDD
    public long SizeKB { get; init; }
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string Scope { get; init; } = "";
    public string RegPath { get; init; } = "";
    public string KeyName { get; init; } = "";
    public bool SystemComponent { get; init; }
    public bool IsUpdate { get; init; }

    public bool Visible => !SystemComponent && !IsUpdate;
    public bool CanUninstall => UninstallString.Length > 0;
    public bool IsMsi => Uninstaller.IsMsiexec(UninstallString);
    public bool NeedsAdmin => RegPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);

    public string PrettyDate => InstallDate.Length == 8
        ? $"{InstallDate[6..8]}/{InstallDate[4..6]}/{InstallDate[..4]}" : InstallDate;
    public string SizeText => SizeKB > 0 ? Uninstaller.HumanSize(SizeKB * 1024) : "";
    public string Detail => string.Join("  ·  ", new[]
        { Publisher, Version.Length > 0 ? "v" + Version : "", PrettyDate, Scope }.Where(s => s.Length > 0));
}

public sealed class Leftover : INotifyPropertyChanged
{
    public const string High = "ALTA", Medium = "MÉDIA", Keep = "NÃO MEXER";

    public string Kind { get; init; } = "";     // "PASTA" ou "REGISTRO"
    public string Path { get; init; } = "";
    public string Confidence { get; set; } = "";
    public string Reason { get; set; } = "";
    public long Bytes { get; set; }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value && CanRemove; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }

    public bool IsFolder => Kind == "PASTA";
    public bool CanRemove => Confidence != Keep;
    public string SizeText => IsFolder ? (Bytes > 0 ? Uninstaller.HumanSize(Bytes) : "vazia") : "";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Uninstaller
{
    public static readonly (string Path, string Scope)[] Roots =
    {
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "máquina 64-bit"),
        (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "máquina 32-bit"),
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall", "só este usuário"),
    };

    public static string BackupFolder => System.IO.Path.Combine(AppSettings.Folder, "backups");

    // ---------------- Registro ----------------

    /// <summary>Abre "HKLM\..." ou "HKCU\..." sempre na visão de 64 bits (o caminho de 32 bits usa WOW6432Node explícito).</summary>
    public static RegistryKey? OpenKey(string path, bool writable = false)
    {
        int i = path.IndexOf('\\');
        if (i < 0) return null;
        RegistryHive hive = path[..i].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => throw new ArgumentException("raiz desconhecida: " + path),
        };
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(path[(i + 1)..], writable);
    }

    public static bool KeyExists(string path)
    {
        try { using var k = OpenKey(path); return k != null; }
        catch { return false; }
    }

    public static List<string> SubKeys(string path)
    {
        try { using var k = OpenKey(path); return k?.GetSubKeyNames().ToList() ?? new(); }
        catch { return new(); }
    }

    public static List<InstalledApp> List()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rootPath, scope) in Roots)
        {
            foreach (var keyName in SubKeys(rootPath))
            {
                var app = Read($@"{rootPath}\{keyName}", keyName, scope);
                if (app == null) continue;
                // O mesmo programa às vezes aparece nas duas visões
                if (seen.Add($"{app.Name}|{app.Version}|{app.Publisher}")) apps.Add(app);
            }
        }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// "Impressão digital" da lista: os nomes das chaves das três raízes. Muda quando qualquer
    /// programa é instalado ou desinstalado (até fora do app). Barato de calcular a cada poucos segundos.
    /// </summary>
    public static string Signature()
    {
        var sb = new StringBuilder();
        foreach (var (rootPath, _) in Roots)
            foreach (var k in SubKeys(rootPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.Append(k).Append('|');
        return sb.ToString();
    }

    /// <summary>Espera a chave do programa sumir (desinstalação concluída). false = cancelado ou tempo esgotado.</summary>
    public static async Task<bool> WaitUntilGoneAsync(string regPath, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!KeyExists(regPath)) return true;
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return !KeyExists(regPath);
    }

    private static InstalledApp? Read(string path, string keyName, string scope)
    {
        try
        {
            using var k = OpenKey(path);
            if (k == null) return null;
            string S(string n) => (k.GetValue(n) as string)?.Trim() ?? "";
            long D(string n) => k.GetValue(n) is int v ? (uint)v : 0;
            string name = S("DisplayName");
            if (name.Length == 0) return null; // sem nome o Windows também não mostra
            string release = S("ReleaseType").ToLowerInvariant();
            return new InstalledApp
            {
                Name = name,
                Version = S("DisplayVersion"),
                Publisher = S("Publisher"),
                InstallDate = S("InstallDate"),
                SizeKB = D("EstimatedSize"),
                InstallLocation = S("InstallLocation").Trim('"'),
                UninstallString = S("UninstallString"),
                QuietUninstallString = S("QuietUninstallString"),
                Scope = scope,
                RegPath = path,
                KeyName = keyName,
                SystemComponent = D("SystemComponent") == 1,
                IsUpdate = S("ParentKeyName").Length > 0 || release.Contains("update") || release.Contains("hotfix"),
            };
        }
        catch { return null; }
    }

    // ---------------- Desinstalar ----------------

    /// <summary>Salva a chave num .reg (dois cliques no arquivo restauram).</summary>
    public static string BackupKey(string regPath, string label)
    {
        Directory.CreateDirectory(BackupFolder);
        string safe = Regex.Replace(label, @"[^\w\-. ]+", "_").Trim();
        if (safe.Length > 60) safe = safe[..60];
        string file = System.IO.Path.Combine(BackupFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safe}.reg");
        var psi = new ProcessStartInfo("reg.exe", $"export \"{regPath}\" \"{file}\" /y")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException("backup da chave falhou: " + err.Trim());
        return file;
    }

    /// <summary>
    /// Roda a UninstallString como o Windows rodaria: pelo cmd, porque ela vem em todo formato
    /// (com e sem aspas, com argumentos). O cmd fica escondido; a janela do desinstalador aparece normal.
    /// </summary>
    public static async Task RunUninstallerAsync(string commandLine)
    {
        string cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var psi = new ProcessStartInfo(cmd, $"/S /C \"{commandLine}\"") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    // ---------------- Caça às sobras ----------------

    private static readonly Regex Brackets = new(@"\([^)]*\)|\[[^\]]*\]");
    private static readonly Regex VersionRx = new(@"(?i)\bv?\d+(\.\d+)+\b");
    private static readonly Regex Arch = new(@"(?i)\b(x64|x86|amd64|arm64|64[- ]?bits?|32[- ]?bits?|version)\b");
    private static readonly Regex PubSuffix = new(@"(?i)\b(inc|ltd|llc|corp|corporation|gmbh|co|company|ltda|s\.?a|limited|software|technologies|group)\b\.?");

    /// <summary>Pastas/chaves de infraestrutura do Windows: nunca viram sobra.</summary>
    private static readonly HashSet<string> Protected = new()
    {
        "windows", "microsoft", "commonfiles", "windowsapps", "packages", "temp", "programs", "internetexplorer",
        "windowsdefender", "windowsnt", "microsoftnet", "referenceassemblies", "msbuild", "classes", "policies",
        "clients", "registeredapplications", "wow6432node", "windowsmail", "windowsmediaplayer", "windowsphotoviewer",
        "modifiablewindowsapps", "uninstallinformation", "installer", "crashdumps", "usoshared", "ssh", "regid19912com",
        "resetexplode",
    };

    public static string CleanName(string s)
    {
        s = Brackets.Replace(s, " ");
        s = VersionRx.Replace(s, " ");
        s = Arch.Replace(s, " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string CleanPublisher(string s)
    {
        s = Brackets.Replace(s, " ");
        s = PubSuffix.Replace(s, " ");
        s = s.Replace(',', ' ').Replace('.', ' ');
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Só letras e dígitos, minúsculo: "7-Zip" vira "7zip".</summary>
    public static string Norm(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) b.Append(c);
        return b.ToString();
    }

    /// <summary>Quantas edições (inserir, apagar, trocar) transformam a em b.</summary>
    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    public static double Similarity(string a, string b)
    {
        int longest = Math.Max(a.Length, b.Length);
        return longest == 0 ? 1 : 1 - (double)Levenshtein(a, b) / longest;
    }

    public sealed record Targets(List<string> Names, string Publisher);

    public static Targets TargetsFor(InstalledApp a)
    {
        string pub = Norm(CleanPublisher(a.Publisher));
        if (pub.Length < 3) pub = "";
        var names = new List<string>();
        void Add(string s)
        {
            string n = Norm(s);
            if (n.Length >= 3 && !Protected.Contains(n) && !names.Contains(n)) names.Add(n);
        }
        string clean = CleanName(a.Name);
        Add(clean);
        // "Mozilla Firefox": também procura só "Firefox"
        if (pub.Length > 0 && Norm(clean).StartsWith(pub)) Add(Norm(clean)[pub.Length..]);
        // Nome da chave, quando não é um GUID tipo {1234-...}
        if (!a.KeyName.StartsWith('{')) Add(CleanName(a.KeyName));
        return new Targets(names, pub);
    }

    /// <summary>Diz se um nome de pasta/chave parece ser do programa, e por quê.</summary>
    public static (string Conf, string Reason)? Match(string candidate, Targets t)
    {
        string c = Norm(candidate);
        // Pasta do editor ("Mozilla") é compartilhada entre apps: nunca é sobra por si só.
        if (c.Length < 3 || Protected.Contains(c) || c == t.Publisher) return null;
        foreach (var n in t.Names)
            if (c == n) return (Leftover.High, "nome idêntico ao do programa");
        foreach (var n in t.Names)
        {
            if (n.Length >= 5 && c.Contains(n)) return (Leftover.Medium, $"o nome contém \"{n}\"");
            if (c.Length >= 5 && n.Contains(c) && (double)c.Length / n.Length >= 0.5)
                return (Leftover.Medium, $"o nome é parte de \"{n}\"");
        }
        double best = 0; string bestName = "";
        foreach (var n in t.Names)
        {
            double s = Similarity(c, n);
            if (s > best) (best, bestName) = (s, n);
        }
        return best >= 0.85 ? (Leftover.Medium, $"nome parecido com \"{bestName}\" ({best:P0})") : null;
    }

    public static List<string> FolderRoots()
    {
        string E(string n) => Environment.GetEnvironmentVariable(n) ?? "";
        string local = E("LOCALAPPDATA");
        var roots = new List<string> { E("ProgramFiles"), E("ProgramFiles(x86)"), E("ProgramW6432"), E("ProgramData"), E("APPDATA"), local };
        if (local.Length > 0) roots.Add(System.IO.Path.Combine(local, "Programs"));
        if (E("USERPROFILE").Length > 0) roots.Add(System.IO.Path.Combine(E("USERPROFILE"), "AppData", "LocalLow"));
        return roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static readonly string[] RegistryRoots = { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" };

    private static string PathKey(string p) => p.TrimEnd('\\', '/').ToLowerInvariant();

    private static bool IsInside(string child, string parent)
    {
        string c = PathKey(child), p = PathKey(parent);
        return c == p || c.StartsWith(p + "\\") || c.StartsWith(p + "/");
    }

    /// <summary>Nunca sugerir "C:\Program Files" inteiro (ou a pasta do usuário) como sobra.</summary>
    public static bool IsRoot(string p)
    {
        string k = PathKey(p);
        if (k.Length <= 3) return true;
        if (FolderRoots().Any(r => PathKey(r) == k)) return true;
        string up = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        return up.Length > 0 && PathKey(up) == k;
    }

    /// <summary>
    /// Procura sobras de <paramref name="app"/>. <paramref name="installed"/> são os programas que
    /// continuam instalados: o que for deles vira "NÃO MEXER".
    /// </summary>
    public static List<Leftover> FindLeftovers(InstalledApp app, IEnumerable<InstalledApp> installed,
        Func<string, List<string>>? subKeys = null, Func<string, bool>? keyExists = null)
    {
        Func<string, List<string>> getSubKeys = subKeys ?? SubKeys;
        Func<string, bool> exists = keyExists ?? KeyExists;
        Targets t = TargetsFor(app);

        // O que pertence a outros programas instalados
        var mine = t.Names.ToHashSet();
        List<(string Path, string Owner)> otherLocations = new();
        Dictionary<string, string> otherNames = new();
        foreach (var o in installed)
        {
            if (o.RegPath == app.RegPath) continue;
            // Tem instalador que registra "C:\Program Files" como local: ignora, senão tudo vira "de outro".
            if (o.InstallLocation.Length > 0 && !IsRoot(o.InstallLocation)) otherLocations.Add((o.InstallLocation, o.Name));
            foreach (var n in TargetsFor(o).Names) if (!mine.Contains(n)) otherNames.TryAdd(n, o.Name);
        }
        string? Owner(string kind, string path)
        {
            if (kind == "PASTA")
                foreach (var (loc, who) in otherLocations)
                    if (IsInside(path, loc) || IsInside(loc, path)) return who;
            string leaf = path.Replace('/', '\\').TrimEnd('\\');
            leaf = leaf[(leaf.LastIndexOf('\\') + 1)..];
            return otherNames.GetValueOrDefault(Norm(leaf));
        }

        Dictionary<string, Leftover> found = new();
        List<string> order = new();
        int Rank(string c) => c switch { Leftover.Keep => 3, Leftover.High => 2, Leftover.Medium => 1, _ => 0 };
        void Add(string kind, string path, string conf, string reason)
        {
            if (Owner(kind, path) is { } who) (conf, reason) = (Leftover.Keep, "pertence a outro programa instalado: " + who);
            string k = kind + "|" + PathKey(path);
            if (found.TryGetValue(k, out var cur))
            {
                if (Rank(conf) > Rank(cur.Confidence)) (cur.Confidence, cur.Reason) = (conf, reason);
                return;
            }
            found[k] = new Leftover { Kind = kind, Path = path, Confidence = conf, Reason = reason };
            order.Add(k);
        }

        // 1) O que o próprio programa declarou
        if (exists(app.RegPath)) Add("REGISTRO", app.RegPath, Leftover.High, "a entrada de desinstalação ainda existe");
        if (app.InstallLocation.Length > 0 && Directory.Exists(app.InstallLocation) && !IsRoot(app.InstallLocation))
            Add("PASTA", app.InstallLocation, Leftover.High, "local de instalação registrado pelo próprio programa");
        string exe = ExePath(app.UninstallString);
        if (exe.Length > 0 && !IsMsiexec(app.UninstallString))
        {
            string? dir = System.IO.Path.GetDirectoryName(exe);
            string win = Environment.GetEnvironmentVariable("SystemRoot") ?? "";
            if (dir != null && Directory.Exists(dir) && !IsRoot(dir) && !(win.Length > 0 && IsInside(dir, win)))
            {
                string b = Norm(System.IO.Path.GetFileName(dir.TrimEnd('\\')));
                if (b != t.Publisher && !Protected.Contains(b))
                    Add("PASTA", dir, Leftover.High, "pasta onde estava o desinstalador");
            }
        }

        // 2) Pastas com nome parecido (e dentro da pasta do editor)
        foreach (var root in FolderRoots())
            foreach (var child in SafeDirs(root))
            {
                string full = System.IO.Path.Combine(root, child);
                if (Match(child, t) is { } m) Add("PASTA", full, m.Conf, m.Reason);
                else if (t.Publisher.Length > 0 && Norm(child) == t.Publisher)
                    foreach (var g in SafeDirs(full))
                        if (Match(g, t) is { } mg) Add("PASTA", System.IO.Path.Combine(full, g), mg.Conf, mg.Reason + " (dentro da pasta do editor)");
            }

        // 3) Chaves de registro com nome parecido
        foreach (var root in RegistryRoots)
            foreach (var k in getSubKeys(root))
            {
                string full = $@"{root}\{k}";
                if (Match(k, t) is { } m) Add("REGISTRO", full, m.Conf, m.Reason);
                else if (t.Publisher.Length > 0 && Norm(k) == t.Publisher)
                    foreach (var g in getSubKeys(full))
                        if (Match(g, t) is { } mg) Add("REGISTRO", $@"{full}\{g}", mg.Conf, mg.Reason + " (dentro da chave do editor)");
            }

        var list = order.Select(k => found[k]).ToList();
        foreach (var l in list)
        {
            if (l.IsFolder) l.Bytes = DirSize(l.Path);
            l.Selected = l.Confidence == Leftover.High; // MÉDIA começa desmarcada: você confere
        }
        return list.OrderByDescending(l => Rank(l.Confidence)).ToList();
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return new DirectoryInfo(path).EnumerateDirectories().Select(d => d.Name).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public static long DirSize(string path)
    {
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            int n = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                total += f.Length;
                if (++n > 50_000) break; // pasta gigante: não trava a tela
            }
        }
        catch { }
        return total;
    }

    // ---------------- Remover sobras (sempre reversível) ----------------

    /// <summary>
    /// Pastas vão para a Lixeira; chaves são exportadas para .reg e depois apagadas.
    /// Retorna (removidos, erros).
    /// </summary>
    public static (int Ok, List<string> Errors) RemoveLeftovers(IEnumerable<Leftover> items, string appName)
    {
        int ok = 0;
        var errors = new List<string>();
        foreach (var l in items.Where(l => l.Selected && l.CanRemove))
        {
            try
            {
                if (l.IsFolder)
                {
                    if (!Directory.Exists(l.Path)) { ok++; continue; }
                    RecycleBin.Send(l.Path);
                }
                else
                {
                    if (!KeyExists(l.Path)) { ok++; continue; }
                    BackupKey(l.Path, appName + " sobra");
                    int i = l.Path.LastIndexOf('\\');
                    using var parent = OpenKey(l.Path[..i], writable: true) ?? throw new IOException("sem acesso à chave");
                    parent.DeleteSubKeyTree(l.Path[(i + 1)..], throwOnMissingSubKey: false);
                }
                ok++;
            }
            catch (Exception ex) { errors.Add($"{l.Path}: {ex.Message}"); }
        }
        return (ok, errors);
    }

    // ---------------- Utilidades ----------------

    /// <summary>Troca %VAR% pelo valor da variável de ambiente.</summary>
    public static string ExpandPercent(string s) => Environment.ExpandEnvironmentVariables(s);

    /// <summary>
    /// Tira o executável de uma UninstallString:
    ///   "C:\Program Files\X\uninst.exe" /S  →  C:\Program Files\X\uninst.exe
    ///   C:\Program Files\X\uninst.exe /S    →  idem (sem aspas, com espaço no caminho)
    ///   MsiExec.exe /X{GUID}                →  MsiExec.exe
    /// </summary>
    public static string ExePath(string commandLine)
    {
        string s = ExpandPercent(commandLine).Trim();
        if (s.Length == 0) return "";
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 0 ? s[1..end] : s.Trim('"');
        }
        string lower = s.ToLowerInvariant();
        int best = -1;
        foreach (var ext in new[] { ".exe", ".bat", ".cmd" })
        {
            int i = lower.IndexOf(ext, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best >= 0 ? s[..(best + 4)] : s.Split(' ')[0];
    }

    public static bool IsMsiexec(string commandLine)
    {
        string b = System.IO.Path.GetFileName(ExePath(commandLine).Replace('/', '\\')).ToLowerInvariant();
        if (b.Contains('\\')) b = b[(b.LastIndexOf('\\') + 1)..];
        return b is "msiexec.exe" or "msiexec";
    }

    public static string HumanSize(long bytes)
    {
        double kb = bytes / 1024.0;
        if (bytes <= 0) return "0 KB";
        if (kb < 1) return "1 KB";
        if (kb < 1024) return $"{kb:0} KB";
        if (kb < 1024 * 1024) return $"{kb / 1024:0.0} MB";
        return $"{kb / 1024 / 1024:0.00} GB";
    }
}

/// <summary>Mandar para a Lixeira (dá para restaurar) em vez de apagar de vez.</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    public static void Send(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0", // a API quer a lista terminada em dois \0 (o marshal põe o segundo)
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int r = SHFileOperationW(ref op);
        if (r != 0 || op.fAnyOperationsAborted) throw new IOException($"não foi para a Lixeira (código 0x{r:X})");
    }

    public static (long Bytes, long Items) Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static void Empty() => SHEmptyRecycleBinW(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // sem confirmação, progresso nem som
}
