using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using RpfPreview.Core;

namespace RpfExplorer.Desktop;

public partial class ExplorerWindow
{
    private MediaSession? playback;
    private AwcInfo? bank;
    private CancellationTokenSource? track;
    private bool userSeek, updatingTime;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private void WirePlayback()
    {
        Play.Click += (_, _) => TogglePlayback();
        StopMedia.Click += (_, _) => playback?.Seek(0, false);
        Step.Click += (_, _) => { if (playback is { } p) p.Seek(p.Position + p.FrameSeconds, false); };
        Volume.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) playback?.Volume(Volume.Value); };
        Timeline.AddHandler(PointerPressedEvent, (_, _) => userSeek = true, RoutingStrategies.Tunnel);
        Timeline.AddHandler(PointerReleasedEvent, (_, _) => { if (userSeek) playback?.Seek(Timeline.Value, playback.Playing); userSeek = false; }, RoutingStrategies.Tunnel, true);
        Timeline.KeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) playback?.Seek(Timeline.Value, playback.Playing); };
        Fullscreen.Click += (_, _) => ToggleFullscreen(); Picture.DoubleTapped += (_, _) => { if (playback?.IsMovie == true) ToggleFullscreen(); };
        KeyDown += (_, e) => { if (WindowState != WindowState.FullScreen || playback == null) return; if (e.Key == Key.Escape) ToggleFullscreen(); else if (e.Key == Key.Space) TogglePlayback(); else if (e.Key is Key.Left or Key.Right) playback.Seek(playback.Position + (e.Key == Key.Left ? -5 : 5), playback.Playing); else return; e.Handled = true; };
        timer.Tick += (_, _) => UpdateTime();
        Hook(ExportAudio, async () => { if (playback?.Wav is not { } wav) return; var path = await Destination("Export decoded WAV", "stream.wav"); if (path != null) await WriteExport(path, await File.ReadAllBytesAsync(wav), default); });
    }
    private void ToggleFullscreen()
    {
        if (playback?.IsMovie != true) return;
        bool full = WindowState != WindowState.FullScreen;
        WindowState = full ? WindowState.FullScreen : WindowState.Normal; BrowserPane.IsVisible = TopBar.IsVisible = MainMenu.IsVisible = Divider.IsVisible = !full;
        Grid.SetColumn(PreviewPane, full ? 0 : 2); Grid.SetColumnSpan(PreviewPane, full ? 3 : 1);
    }
    private void TogglePlayback() { if (playback is not { } p) return; if (p.Playing) p.Pause(); else p.Seek(p.Position >= p.Duration - .01 ? 0 : p.Position, true); UpdateTime(); }
    private void UpdateTime()
    {
        if (playback is not { } p || updatingTime) return;
        updatingTime = true;
        try
        {
            if (p.Playing && p.Position >= p.Duration) { p.Seek(Loop.IsChecked == true ? 0 : p.Duration, Loop.IsChecked == true); }
            if (!userSeek) Timeline.Value = p.Position; Play.Content = p.Playing ? "Pause" : "Play";
            Clock.Text = $"  {TimeSpan.FromSeconds(p.Position):mm\\:ss} / {TimeSpan.FromSeconds(p.Duration):mm\\:ss}";
        }
        finally { updatingTime = false; }
    }
    private void StopPlayback(bool resetBank = true)
    {
        if (WindowState == WindowState.FullScreen && playback?.IsMovie == true) ToggleFullscreen();
        timer.Stop(); track?.Cancel(); track = null; playback?.Dispose(); playback = null;
        if (resetBank) bank = null; MediaBar.IsVisible = false; ExportAudio.IsEnabled = false;
    }
    private void Attach(MediaSession session, CancellationToken token)
    {
        if (token.IsCancellationRequested) { session.Dispose(); return; }
        playback = session;
        session.Frame += p => { if (playback == session) SetBitmap(Pixels(p)); };
        session.Failed += message => { if (playback == session) Status.Text = "Playback stopped · " + message; };
        session.Volume(Volume.Value); Timeline.Maximum = Math.Max(.001, session.Duration); Timeline.Value = 0; MediaBar.IsVisible = true;
        Step.IsVisible = Fullscreen.IsVisible = session.IsMovie; Volume.IsEnabled = session.AudioAvailable;
        session.Seek(0, false); timer.Start(); UpdateTime(); UpdateToolStates();
    }
    private async Task SelectBank(WorkspaceAsset a, CancellationToken token)
    {
        var info = await Task.Run(() => AwcInfo.Inspect(a.SourcePath, a.Entry.Offset, a.Entry.Size), token) ?? throw new InvalidDataException("Invalid audio bank."); token.ThrowIfCancellationRequested(); bank = info;
        var streams = info.Streams.ToArray(); int first = Array.FindIndex(streams, s => s.SupportsPreview);
        SetChoices(streams.Select(s => new ChoiceItem(s.Index, s.Label(a.AudioCues))).ToArray(), first);
        if (first >= 0) await OpenTrack(a, streams[first].Index, token); else Details.Text = "This bank contains no supported stream.";
    }
    private async Task OpenTrack(WorkspaceAsset a, int index, CancellationToken token)
    {
        StopPlayback(false); using var next = CancellationTokenSource.CreateLinkedTokenSource(token); track = next;
        try
        {
            var s = bank!.Streams.Single(s => s.Index == index); if (!s.SupportsPreview) throw new NotSupportedException($"Unsupported audio codec {s.Codec:X2}.");
            Details.Text = s.Label(a.AudioCues) + $"\n{s.Channels} channels · {s.DecodedSampleRate:N0} Hz"; Attach(await MediaSession.Bank(a, s, next.Token), next.Token);
        }
        finally { if (track == next) track = null; }
    }
    private async Task OpenWave(string path, CancellationToken token) { Attach(await MediaSession.Wave(path, token), token); Details.Text = "Staged audio preview"; }
    private async Task OpenMovie(WorkspaceAsset a, VideoInfo info, CancellationToken token)
    {
        Details.Text = $"{info.Width} × {info.Height} · {info.FramesPerSecond:0.##} fps · {info.AudioTracks} audio tracks\nOpening movie…";
        Attach(await MediaSession.Movie(a, info, token), token);
    }
}
