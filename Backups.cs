using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ResetExplode;

// Backups .reg que o Desinstalar faz antes de mexer em qualquer chave do registro.
// Nome do arquivo: AAAAMMDD-HHMMSS_<rótulo>.reg   (rótulo terminado em " sobra" = backup de uma sobra)

public sealed class RegBackup
{
    public string File { get; init; } = "";
    public DateTime When { get; init; }
    public string Label { get; init; } = "";
    public string Key { get; init; } = "";
    public long Bytes { get; init; }
    public bool IsLeftover { get; init; }

    public string WhenText => When.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
    public string KindText => IsLeftover ? "SOBRA" : "DESINSTALAÇÃO";
    public string Hint => IsLeftover
        ? "Restaurar devolve esta chave do registro exatamente como estava."
        : "Restaurar só devolve a entrada na lista de programas; os arquivos do programa já foram removidos. Para ter o programa de volta, reinstale.";
}

public static class Backups
{
    public static List<RegBackup> List(string? folder = null)
    {
        folder ??= Uninstaller.BackupFolder;
        if (!Directory.Exists(folder)) return new();
        return new DirectoryInfo(folder).EnumerateFiles("*.reg")
            .Select(f => Parse(f.FullName, f.Length, f.LastWriteTime))
            .OrderByDescending(b => b.When).ToList();
    }

    public static RegBackup Parse(string file, long bytes, DateTime fallback)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        DateTime when = fallback;
        string label = name;
        int us = name.IndexOf('_');
        if (us == 15 && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            (when, label) = (d, name[16..]);
        bool leftover = label.EndsWith(" sobra", StringComparison.OrdinalIgnoreCase);
        if (leftover) label = label[..^6];
        return new RegBackup { File = file, When = when, Label = label.Trim(), Key = FirstKey(file), Bytes = bytes, IsLeftover = leftover };
    }

    /// <summary>A primeira linha "[HKEY_...]" do .reg (o reg.exe grava em UTF-16).</summary>
    public static string FirstKey(string file)
    {
        try
        {
            using var r = new StreamReader(file, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            for (int i = 0; i < 20 && r.ReadLine() is { } line; i++)
                if (line.StartsWith("[HKEY_")) return line.Trim('[', ']');
        }
        catch { }
        return "";
    }

    /// <summary>Importa o .reg de volta (reg import).</summary>
    public static void Restore(RegBackup b)
    {
        var psi = new ProcessStartInfo("reg.exe", $"import \"{b.File}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException("reg import falhou: " + err.Trim());
    }
}
