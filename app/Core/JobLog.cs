using System.Text;

namespace Oplus.Core;

/// <summary>
/// The job log: newlog.csv in the data folder with the header
/// Date,Action,Printer Name,User,MAC address,File Name,Page; each line starts with the local time
///. Past 1 GiB it moves on to newlog-1.csv, newlog-2.csv, ...
/// </summary>
public static class JobLog
{
    static readonly object Gate = new();
    static string? _path;
    public static event Action? Changed;

    public static string CurrentPath
    {
        get
        {
            lock (Gate)
            {
                if (_path != null) return _path;
                _path = Path.Combine(SettingsStore.DataFolder, "newlog.csv");
                int n = 0;
                while (File.Exists(_path) && new FileInfo(_path).Length >= Defaults.LogRollBytes)
                    _path = Path.Combine(SettingsStore.DataFolder, $"newlog-{++n}.csv");
                return _path;
            }
        }
    }

    public static void Write(string action, string printer, string user, string mac, string file, int pages)
    {
        if (SettingsStore.ReadOnly) return;
        lock (Gate)
        {
            try
            {
                var path = CurrentPath;
                if (File.Exists(path) && new FileInfo(path).Length >= Defaults.LogRollBytes) { _path = null; path = CurrentPath; }
                bool isNew = !File.Exists(path);
                var sb = new StringBuilder();
                if (isNew) sb.Append("Date,Action,Printer Name,User,MAC address,File Name,Page\n");
                sb.Append(DateTime.Now.ToString("yyyyMMdd-HHmmss")).Append(',');
                sb.Append(string.Join(",", new[] { action, printer, user, mac, file }.Select(Clean))).Append(',').Append(pages).Append('\n');
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
        Changed?.Invoke();
    }

    static string Clean(string s) => s.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ');

    public static string ReadAll()
    {
        lock (Gate)
        {
            var dir = SettingsStore.DataFolder;
            var files = Directory.GetFiles(dir, "newlog*.csv").OrderBy(f => f.Length).ThenBy(f => f).ToList();
            if (files.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                try { using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var r = new StreamReader(fs); sb.Append(r.ReadToEnd()); }
                catch { }
            }
            return sb.ToString();
        }
    }
}
