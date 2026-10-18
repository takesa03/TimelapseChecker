using System;
using System.IO;
using System.Text.Json;
using TimelapseChecker.Core;

namespace TimelapseChecker.App.Services;

public static class AppPaths
{
    // %AppData% はパッケージ仮想化の対象になり実体が変わることがあるため、
    // SwitchBotMeterと同様に exe と同じ場所へ設定を保存する。
    public static string SettingsDirectory
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "settings");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(SettingsDirectory, "log");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

public class AppSettings
{
    /// <summary>監視するタイムラプス出力フォルダ。</summary>
    public string WatchFolder { get; set; } = "";

    /// <summary>対象とする拡張子（セミコロン区切り）。</summary>
    public string Extensions { get; set; } = ".tif;.tiff;.png;.jpg;.jpeg";

    public bool SoundEnabled { get; set; } = true;
    public bool SoundOnCaution { get; set; } = false;

    /// <summary>警告音のwavファイル。空なら標準のシステム音。</summary>
    public string SoundFilePath { get; set; } = "";

    /// <summary>警告が続く間、音を繰り返す間隔（秒）。</summary>
    public int RepeatSeconds { get; set; } = 10;

    public bool AutoStart { get; set; } = false;

    /// <summary>YouTube配信用レイアウトで起動するか。</summary>
    public bool StreamLayout { get; set; } = false;

    /// <summary>配信用レイアウトのウィンドウサイズ（物理ピクセル）。1920x1080（タスクバー48px）で幅646・高さ1032。0以下の高さは作業領域の高さいっぱい。</summary>
    public int StreamWindowWidth { get; set; } = 646;
    public int StreamWindowHeight { get; set; } = 1032;

    /// <summary>画面の「撮影 N 枚」に数える、監視フォルダ内のファイルの拡張子（セミコロン区切り）。</summary>
    public string CountExtensions { get; set; } = ".tif;.tiff";

    /// <summary>基準画像・最新画像のプレビューを元画像（カラー）で表示するか。falseなら比較用（解析に使うグレースケール）。</summary>
    public bool ShowOriginalImage { get; set; } = true;

    public CheckerThresholds Thresholds { get; set; } = new();
}

public class WindowSettings
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

public class JsonStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string filePath;

    public JsonStore(string fileName)
    {
        filePath = Path.Combine(AppPaths.SettingsDirectory, fileName);
    }

    public T Load()
    {
        if (!File.Exists(filePath)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(filePath)) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    public bool Exists => File.Exists(filePath);

    public void Save(T value)
    {
        try
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(value, Options));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"設定保存エラー: {ex.Message}");
        }
    }
}


