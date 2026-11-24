using System.Diagnostics;
using System.Globalization;
using RpfPreview.Core;

namespace RpfExplorer.Desktop;

internal sealed class MediaSession : IDisposable
{
    private readonly string directory;
    private readonly VideoInfo? video;
    private readonly string? movie;
    private Process? audio;
    private CancellationTokenSource? frames;
    private readonly Stopwatch clock = new();
    private double position;
    private bool disposed;
    public string? Wav { get; private set; }
    public bool IsMovie => video != null;
    public double Duration { get; private set; }
    public double FrameSeconds => 1 / (video?.FramesPerSecond ?? 30);
    public bool Playing => clock.IsRunning;
    public double Position => Math.Min(Duration, position + clock.Elapsed.TotalSeconds);
    public bool AudioAvailable => audio is { HasExited: false };
    public event Action<RageTexturePixels>? Frame;
    public event Action<string>? Failed;
    private MediaSession(string dir, string? movie, VideoInfo? video)
    { directory = dir; this.movie = movie; this.video = video; Duration = video?.DurationSeconds ?? 0; }
    private static string NewFolder() => Directory.CreateTempSubdirectory("rpf9-media-").FullName;
    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static Process Start(string tool, IEnumerable<string> args, bool commands = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tools", tool);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = commands };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Cannot start " + tool);
    }
    private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
    private static async Task Run(string tool, IEnumerable<string> args, CancellationToken token)
    {
        using var p = Start(tool, args); using var cancel = token.Register(() => Kill(p));
        var errors = p.StandardError.ReadToEndAsync(); var output = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync(); await output; string error = await errors; token.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new InvalidDataException(error.Trim() is { Length: > 0 } msg ? msg : tool + " failed.");
    }
    public static async Task<MediaSession> Movie(WorkspaceAsset asset, VideoInfo info, CancellationToken token)
    {
        string dir = NewFolder(); string file = Path.Combine(dir, "movie.bik"); var session = new MediaSession(dir, file, info);
        try
        {
            await using (var input = File.Open(asset.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var output = File.Create(file))
            {
                input.Position = asset.Entry.Offset; long left = asset.Entry.Size; byte[] buffer = new byte[131072];
                while (left > 0) { int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token); if (read == 0) throw new EndOfStreamException(); await output.WriteAsync(buffer.AsMemory(0, read), token); left -= read; }
            }
            if (info.AudioTracks > 0)
            {
                var wav = Path.Combine(dir, "audio.wav");
                await Run("ffmpeg", ["-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe", "-i", file, "-map", "0:a:0", "-vn", "-ac", "2", "-c:a", "pcm_s16le", wav], token);
                await session.AttachAudio(wav, token);
            }
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public static async Task<MediaSession> Bank(WorkspaceAsset a, AwcStreamInfo stream, CancellationToken token)
    {
        string dir = NewFolder(); var session = new MediaSession(dir, null, null);
        try
        {
            var wav = Path.Combine(dir, "stream.wav");
            if (stream.Codec == 5)
            {
                var xma = Path.Combine(dir, "xma.wav"); await Task.Run(() => AwcRecovery.WriteXmaWave(a.SourcePath, a.Entry.Offset, a.Entry.Size, stream.Index, xma), token);
                await Run("ffmpeg", ["-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe", "-i", xma, "-t", "120", "-c:a", "pcm_s16le", wav], token);
            }
            else await Run("opus-preview", [a.SourcePath, a.Entry.Offset.ToString(), a.Entry.Size.ToString(), stream.Index.ToString(), wav, "120"], token);
            await session.AttachAudio(wav, token); return session;
        }
        catch { session.Dispose(); throw; }
    }
    public static async Task<MediaSession> Wave(string path, CancellationToken token)
    {
        string dir = NewFolder(); var session = new MediaSession(dir, null, null);
        try
        {
            var wav = Path.Combine(dir, "preview.wav"); await Run("ffmpeg", ["-v", "error", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", path, "-t", "120", "-c:a", "pcm_s16le", wav], token);
            await session.AttachAudio(wav, token); return session;
        }
        catch { session.Dispose(); throw; }
    }
    private async Task AttachAudio(string file, CancellationToken token)
    {
        Wav = file;
        if (video == null)
        {
            using var reader = new BinaryReader(File.OpenRead(file)); reader.BaseStream.Position = 12; uint rate = 0;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string tag = new(reader.ReadChars(4)); uint n = reader.ReadUInt32(); long start = reader.BaseStream.Position;
                if (n > reader.BaseStream.Length - start) break;
                if (tag == "fmt " && n >= 16) { reader.BaseStream.Position += 8; rate = reader.ReadUInt32(); }
                if (tag == "data" && rate > 0) { Duration = n / (double)rate; break; }
                reader.BaseStream.Position = start + n + (n & 1);
            }
            if (Duration <= 0) throw new InvalidDataException("The decoded WAV has no playable samples.");
        }
        audio = Start("audio-output", [file], true); audio.BeginErrorReadLine();
        using var cancel = token.Register(() => Kill(audio));
        var ready = await audio.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(12), token);
        if (ready != "READY") { Kill(audio); audio.Dispose(); audio = null; if (!IsMovie) throw new IOException("Cannot open the audio output device."); }
    }
    private void Send(string command)
    {
        if (!AudioAvailable) return;
        try { audio!.StandardInput.WriteLine(command); audio.StandardInput.Flush(); } catch (IOException) { }
    }
    public void Volume(double v) => Send("VOLUME " + Number(v));
    public void Seek(double seconds, bool play)
    {
        if (disposed) return;
        position = Math.Clamp(seconds, 0, Duration); clock.Reset(); Send("PAUSE"); Send("SEEK " + Number(position));
        frames?.Cancel(); if (video == null) { if (play) { clock.Start(); Send("PLAY"); } return; }
        var source = new CancellationTokenSource(); frames = source; _ = ReadFrames(position, play, source);
    }
    public void Pause() { Seek(Position, false); }
    private async Task ReadFrames(double start, bool play, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            var v = video!; double scale = Math.Min(1, Math.Min(960d / v.Width, 720d / v.Height));
            int width = Math.Max(1, (int)(v.Width * scale)), height = Math.Max(1, (int)(v.Height * scale));
            var args = new List<string> { "-v", "error", "-nostdin", "-protocol_whitelist", "file,pipe", "-ss", Number(Math.Min(start, Math.Max(0, Duration - FrameSeconds))), "-i", movie!, "-map", "0:v:0", "-an", "-vf", $"scale={width}:{height}", "-pix_fmt", "bgra", "-threads", "1" };
            if (!play) args.AddRange(["-frames:v", "1"]); args.AddRange(["-f", "rawvideo", "pipe:1"]);
            using var p = Start("ffmpeg", args); using var cancel = token.Register(() => Kill(p));
            var errors = p.StandardError.ReadToEndAsync(); using var output = p.StandardOutput.BaseStream; var buffer = new byte[checked(width * height * 4)];
            for (int i = 0; ; i++)
            {
                int got = await output.ReadAtLeastAsync(buffer, buffer.Length, false, token);
                if (got == 0) break; if (got != buffer.Length) throw new InvalidDataException("Truncated movie frame.");
                if (i == 0 && play) { clock.Restart(); Send("PLAY"); }
                if (play) { double delay = i * FrameSeconds - clock.Elapsed.TotalSeconds; if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token); }
                token.ThrowIfCancellationRequested(); Frame?.Invoke(new(width, height, buffer));
            }
            await p.WaitForExitAsync(token); string error = await errors; if (p.ExitCode != 0) throw new InvalidDataException(error.Trim());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { clock.Stop(); Send("PAUSE"); Failed?.Invoke(ex.Message); } }
        finally { if (frames == source) frames = null; source.Dispose(); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; frames?.Cancel(); clock.Stop();
        if (audio != null) { Kill(audio); audio.Dispose(); audio = null; }
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
