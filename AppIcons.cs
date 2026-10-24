using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResetExplode;

/// <summary>
/// Ícone de cada programa da lista do Desinstalar, tirado do DisplayIcon do registro
/// ("C:\...\app.exe,0" ou um .ico). Carrega em segundo plano e guarda em cache.
/// </summary>
public static class AppIcons
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>Preenche InstalledApp.Icon dos que ainda não têm. Pode chamar várias vezes.</summary>
    public static void LoadAsync(IEnumerable<InstalledApp> apps)
    {
        var pending = apps.Where(a => a.Icon == null && !a.IsStore && a.DisplayIcon.Length > 0).ToList();
        if (pending.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var app in pending)
            {
                var img = Get(app.DisplayIcon);
                if (img != null) Application.Current?.Dispatcher.BeginInvoke(() => app.Icon = img);
            }
        });
    }

    private static ImageSource? Get(string displayIcon)
    {
        lock (Lock) if (Cache.TryGetValue(displayIcon, out var hit)) return hit;
        ImageSource? img = null;
        try { img = Extract(displayIcon); } catch { }
        lock (Lock) Cache[displayIcon] = img;
        return img;
    }

    private static ImageSource? Extract(string displayIcon)
    {
        // "C:\x\app.exe,-101" / "\"C:\x\app.exe\",0" / "C:\x\app.ico"
        string s = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        int index = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out int idx)) { index = idx; s = s[..comma]; }
        s = s.Trim().Trim('"');
        if (!File.Exists(s)) return null;

        var large = new IntPtr[1];
        if (ExtractIconExW(s, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze(); // pode atravessar threads
            return src;
        }
        finally { DestroyIcon(large[0]); }
    }
}
