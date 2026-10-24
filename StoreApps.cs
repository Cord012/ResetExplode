using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ResetExplode;

// Apps da Microsoft Store (pacotes AppX/MSIX): não ficam nas chaves Uninstall, então a lista do
// "Desinstalar" não os via. Aqui:
//  - a lista vem do PowerShell (Get-AppxPackage): é ele que sabe o que é framework, o que é do
//    sistema e o que não pode ser removido;
//  - o nome amigável vem do registro do AppModel (o mesmo lugar que o Geek Uninstaller lê),
//    resolvido com SHLoadIndirectString ("@{Pacote?ms-resource://...}" vira "Spotify");
//  - desinstalar é Remove-AppxPackage (só para o usuário atual, como o Windows faz).
public static class StoreApps
{
    public const string RepositoryRoot =
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private sealed class Pkg
    {
        public string Name { get; set; } = "";
        public string PackageFullName { get; set; } = "";
        public string PackageFamilyName { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Version { get; set; } = "";
        public string InstallLocation { get; set; } = "";
    }

    // Só apps "de verdade": nada de framework, pacote de recurso, item não removível ou assinado pelo sistema.
    private const string ListScript =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
        "$p=@(Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable " +
        "-and -not $_.IsResourcePackage -and ($_.SignatureKind -eq 'Store' -or $_.SignatureKind -eq 'Developer') } | " +
        "Select-Object Name,PackageFullName,PackageFamilyName,Publisher,@{n='Version';e={[string]$_.Version}},InstallLocation);" +
        "ConvertTo-Json -InputObject $p -Compress";

    public static List<InstalledApp> List()
    {
        string json = RunPowerShell(ListScript, TimeSpan.FromSeconds(60));
        return Parse(json, DisplayNameFromRegistry);
    }

    /// <summary>Converte o JSON do PowerShell. Separado para poder testar sem Windows.</summary>
    public static List<InstalledApp> Parse(string json, Func<string, string?> displayName)
    {
        var list = new List<InstalledApp>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        var pkgs = JsonSerializer.Deserialize<List<Pkg>>(json.Trim(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        foreach (var p in pkgs.Where(p => p.PackageFullName.Length > 0))
        {
            string name = displayName(p.PackageFullName) is { Length: > 0 } dn && !dn.StartsWith("ms-resource:") && !dn.StartsWith("@{")
                ? dn : PrettyName(p.Name);
            list.Add(new InstalledApp
            {
                Name = name,
                Version = p.Version,
                Publisher = PublisherName(p.Publisher),
                InstallLocation = p.InstallLocation,
                Scope = "Microsoft Store",
                RegPath = $@"{RepositoryRoot}\{p.PackageFullName}",
                KeyName = p.PackageFullName,
                IsStore = true,
                PackageFullName = p.PackageFullName,
                PackageFamilyName = p.PackageFamilyName,
            });
        }
        return list;
    }

    /// <summary>"SpotifyAB.SpotifyMusic" vira "Spotify Music"; "Microsoft.WindowsCalculator" vira "Windows Calculator".</summary>
    public static string PrettyName(string id)
    {
        string last = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
        var sb = new StringBuilder();
        for (int i = 0; i < last.Length; i++)
        {
            if (i > 0 && char.IsUpper(last[i]) && char.IsLower(last[i - 1])) sb.Append(' ');
            sb.Append(last[i]);
        }
        return sb.Length > 0 ? sb.ToString() : id;
    }

    /// <summary>"CN=Spotify AB, O=Spotify AB, C=SE" vira "Spotify AB". CN que é só um código (GUID) some.</summary>
    public static string PublisherName(string publisher)
    {
        foreach (var part in publisher.Split(','))
        {
            var kv = part.Trim();
            if (kv.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                string cn = kv[3..].Trim('"', ' ');
                return Guid.TryParse(cn, out _) ? "" : cn;
            }
        }
        return "";
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int outSize, IntPtr reserved);

    private static string? DisplayNameFromRegistry(string packageFullName)
    {
        try
        {
            using var k = Uninstaller.OpenKey($@"{RepositoryRoot}\{packageFullName}");
            if (k?.GetValue("DisplayName") is not string raw || raw.Length == 0) return null;
            if (!raw.StartsWith("@")) return raw;
            var sb = new StringBuilder(512);
            return SHLoadIndirectString(raw, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    public static async Task RemoveAsync(string packageFullName)
    {
        // Nome do pacote só tem letras, números, ponto, _ e -: nada que quebre a aspa simples.
        if (packageFullName.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '~')))
            throw new ArgumentException("nome de pacote inválido");
        await Task.Run(() => RunPowerShell($"Remove-AppxPackage -Package '{packageFullName}'", TimeSpan.FromMinutes(10)));
    }

    /// <summary>App da Store é isolado: a sobra possível é a pasta de dados em %LOCALAPPDATA%\Packages.</summary>
    public static List<Leftover> FindLeftovers(InstalledApp app, Func<string, bool> keyExists)
    {
        var list = new List<Leftover>();
        if (keyExists(app.RegPath))
            list.Add(new Leftover { Kind = "REGISTRO", Path = app.RegPath, Confidence = Leftover.Keep,
                                    Reason = "o pacote ainda está registrado no Windows (desinstale primeiro)" });
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (app.PackageFamilyName.Length > 0 && local.Length > 0)
        {
            string data = Path.Combine(local, "Packages", app.PackageFamilyName);
            if (Directory.Exists(data))
                list.Add(new Leftover { Kind = "PASTA", Path = data, Confidence = Leftover.High,
                                        Reason = "pasta de dados do app (configurações e cache)", Bytes = Uninstaller.DirSize(data) });
        }
        foreach (var l in list) l.Selected = l.Confidence == Leftover.High;
        return list;
    }

    private static string RunPowerShell(string script, TimeSpan timeout)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        // -EncodedCommand: o script vai em Base64 (UTF-16), sem problema de aspas.
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new IOException("não consegui abrir o PowerShell");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("o PowerShell demorou demais");
        }
        string err = stderr.Result.Trim();
        if (p.ExitCode != 0 && err.Length > 0) throw new IOException(err.Split('\n')[0]);
        return stdout.Result;
    }
}
