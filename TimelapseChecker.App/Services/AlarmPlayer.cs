using System;
using System.IO;
using System.Media;
using System.Windows.Media;
using System.Windows.Threading;

namespace TimelapseChecker.App.Services;

/// <summary>
/// 警告音の再生。開始後は確認(Stop)されるまで一定間隔で繰り返す。
/// Windowsのサウンド設定（「無音」等）に左右されないよう、既定音はアプリ内で生成した
/// 警告トーンを再生する。ファイルを指定した場合は MediaPlayer で再生する（wav/mp3等）。
/// </summary>
public sealed class AlarmPlayer
{
    private readonly DispatcherTimer timer = new();
    private readonly SoundPlayer defaultTone;
    private MediaPlayer? filePlayer;
    private string soundFile = "";

    public bool IsRunning { get; private set; }

    /// <summary>直近の再生エラー（ファイルを開けない等）。UI表示用。</summary>
    public event Action<string>? Error;

    public AlarmPlayer()
    {
        defaultTone = new SoundPlayer(new MemoryStream(CreateToneWav()));
        defaultTone.Load();
        timer.Tick += (_, _) => PlayOnce();
    }

    public void Start(string soundFilePath, int repeatSeconds)
    {
        soundFile = soundFilePath;
        timer.Interval = TimeSpan.FromSeconds(Math.Max(1, repeatSeconds));
        IsRunning = true;
        PlayOnce();
        timer.Start();
    }

    public void Stop()
    {
        timer.Stop();
        IsRunning = false;
        try { filePlayer?.Stop(); } catch { }
        try { defaultTone.Stop(); } catch { }
    }

    public void SetSoundFile(string path) => soundFile = path;

    public void PlayOnce()
    {
        if (!string.IsNullOrWhiteSpace(soundFile))
        {
            if (File.Exists(soundFile))
            {
                if (TryPlayFile(soundFile)) return;
            }
            else
            {
                Error?.Invoke($"警告音ファイルが見つかりません: {soundFile}（標準の警告音を使用します）");
            }
        }

        PlayDefaultTone();
    }

    private bool TryPlayFile(string path)
    {
        try
        {
            if (filePlayer == null)
            {
                filePlayer = new MediaPlayer();
                filePlayer.MediaFailed += (_, e) =>
                {
                    Error?.Invoke($"警告音ファイルを再生できません: {e.ErrorException?.Message}（標準の警告音を使用します）");
                    PlayDefaultTone();
                };
            }
            filePlayer.Open(new Uri(path, UriKind.Absolute));
            filePlayer.Volume = 1.0;
            filePlayer.Play();
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"警告音ファイルを再生できません: {ex.Message}（標準の警告音を使用します）");
            return false;
        }
    }

    private void PlayDefaultTone()
    {
        try
        {
            defaultTone.Play();
        }
        catch
        {
            SystemSounds.Beep.Play();
        }
    }

    /// <summary>2音を交互に鳴らす約1.8秒の警告トーン(16bit/44.1kHz/モノラルPCM)を生成する。</summary>
    private static byte[] CreateToneWav()
    {
        const int sampleRate = 44100;
        const double toneSec = 0.22;
        const double gapSec = 0.06;
        double[] freqs = { 988, 740, 988, 740, 988, 740 };

        var samples = new System.Collections.Generic.List<short>();
        foreach (double f in freqs)
        {
            int n = (int)(sampleRate * toneSec);
            for (int i = 0; i < n; i++)
            {
                // クリックノイズを避けるため、両端を短くフェードさせる
                double env = Math.Min(1.0, Math.Min(i, n - i) / (sampleRate * 0.01));
                double v = Math.Sin(2 * Math.PI * f * i / sampleRate) * 0.8 * env;
                samples.Add((short)(v * short.MaxValue));
            }
            samples.AddRange(new short[(int)(sampleRate * gapSec)]);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Count * 2;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);    // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
