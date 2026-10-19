using System.Runtime.InteropServices;

namespace AudioMixer.Core.Sonar;

/// <summary>A named sound for one channel: from Sonar's library or saved by you.</summary>
public sealed record SoundPreset(string Name, Channel Channel, SoundSettings Settings, bool FromSonar, bool IsSelectedInSonar = false);

/// <summary>
/// Reads the presets that SteelSeries GG installed (and any you made in Sonar) from Sonar's database.
/// Nothing is copied into this app: the database is read at run time from the user's own GG install,
/// through a temporary copy so Sonar's file is never locked or changed. Uses Windows' built-in SQLite.
/// </summary>
public static class SonarPresets
{
    public static readonly string DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SteelSeries", "GG", "apps", "sonar", "db", "database.db");

    static Channel? ChannelFromVad(long vad) => vad switch
    {
        1 => Channel.Game,
        2 => Channel.Chat,
        3 => Channel.Mic,
        4 => Channel.Media,
        5 => Channel.Aux,
        _ => null,
    };

    /// <summary>All presets in Sonar's database; empty when GG/Sonar isn't installed or the file can't be read.</summary>
    public static IReadOnlyList<SoundPreset> Load()
    {
        if (!File.Exists(DatabasePath))
            return [];
        var dir = Path.Combine(Path.GetTempPath(), "AudioMixer-sonar-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, "database.db");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(DatabasePath + suffix))
                    CopyShared(DatabasePath + suffix, copy + suffix);
            return Read(copy);
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    // Sonar may have the database open; copy with full sharing instead of File.Copy.
    static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = File.Create(to);
        source.CopyTo(target);
    }

    static List<SoundPreset> Read(string path)
    {
        var presets = new List<SoundPreset>();
        if (Sqlite.sqlite3_open_v2(path, out var db, Sqlite.OpenReadWrite, IntPtr.Zero) != 0)
            return presets;
        try
        {
            var selected = new HashSet<string>();
            Query(db, "SELECT config_id FROM selected_config", row => selected.Add(row(0)));
            Query(db, "SELECT id, name, vad, data, is_preset FROM configs ORDER BY is_preset, name", row =>
            {
                if (ChannelFromVad(long.TryParse(row(2), out var v) ? v : 0) is not { } channel)
                    return;
                try
                {
                    var settings = SoundSettings.FromJson(row(3));
                    presets.Add(new SoundPreset(row(1), channel, settings, FromSonar: true, IsSelectedInSonar: selected.Contains(row(0))));
                }
                catch (Exception)
                {
                    // A config with an older or unknown shape: skip it.
                }
            });
        }
        finally
        {
            Sqlite.sqlite3_close(db);
        }
        return presets;
    }

    static void Query(IntPtr db, string sql, Action<Func<int, string>> onRow)
    {
        if (Sqlite.sqlite3_prepare16_v2(db, sql, -1, out var stmt, IntPtr.Zero) != 0)
            return;
        try
        {
            while (Sqlite.sqlite3_step(stmt) == Sqlite.Row)
                onRow(i => Marshal.PtrToStringUni(Sqlite.sqlite3_column_text16(stmt, i)) ?? "");
        }
        finally
        {
            Sqlite.sqlite3_finalize(stmt);
        }
    }

    static class Sqlite
    {
        public const int OpenReadWrite = 0x2;
        public const int Row = 100;
        const string Lib = "winsqlite3";

        [DllImport(Lib, CharSet = CharSet.Ansi)]
        public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Lib)]
        public static extern int sqlite3_close(IntPtr db);

        [DllImport(Lib, CharSet = CharSet.Unicode)]
        public static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);

        [DllImport(Lib)]
        public static extern int sqlite3_step(IntPtr stmt);

        [DllImport(Lib)]
        public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int column);

        [DllImport(Lib)]
        public static extern int sqlite3_finalize(IntPtr stmt);
    }
}
