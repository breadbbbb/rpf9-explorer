using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using RpfPreview.Core;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace RpfExplorer.Desktop;

public partial class ExplorerWindow
{
    private string? folder, container;
    private CancellationTokenSource thumbnails = new();
    private readonly List<Bitmap> thumbnailImages = [];
    private static string ThemeFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rpf9-explorer", "appearance");
    private WorkspaceAsset[] SelectedRows() => Assets.SelectedItems?.OfType<Row>().Select(r => r.Asset).ToArray() ?? [];
    private void SaveTheme()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!); File.WriteAllText(ThemeFile, Dark.IsChecked == true ? "dark" : "light"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
    }

    private void WireTools()
    {
        Type.ItemsSource = new[] { "all", "image", "model", "audio", "video", "metadata", "shader", "animation", "archive" };
        Type.SelectedIndex = 0;
        try { Dark.IsChecked = !File.Exists(ThemeFile) || File.ReadAllText(ThemeFile) != "light"; }
        catch (IOException) { }
        RequestedThemeVariant = Dark.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
        Type.SelectionChanged += async (_, _) => { if (!updating) await Filter(); };
        Only.IsCheckedChanged += async (_, _) => { if (!updating) await Filter(); };
        Gallery.IsCheckedChanged += async (_, _) => { if (!updating) await Filter(); };
        GlobalSearch.IsCheckedChanged += async (_, _) => { if (!updating) await Filter(); };
        Folders.SelectionChanged += async (_, _) =>
        {
            if (updating || Folders.SelectedItem is not Place place) return;
            folder = place.Folder; group = place.Group; container = null; await Filter();
        };
        FolderUp.Click += async (_, _) => { container = null; folder = folder?.Contains('/') == true ? folder[..folder.LastIndexOf('/')] : null; group = null; FillFolders(CurrentSource); await Filter(); };
        Hook(Clear, async () => { project = null; projects.Clear(); edited.Clear(); parts.Clear(); group = null; await SetWorkspace([], AssetWorkspaceIndex.Build([]), null); Status.Text = "Workspace cleared."; });
        Hook(Recovery, () => { decoder = null; Status.Text = "New archives will open in recovery mode."; return Task.CompletedTask; });
        Hook(Report, async () =>
        {
            var doc = CurrentSource ?? documents.FirstOrDefault(); if (doc == null) return;
            var path = await Destination("Save inspection report", "inspection.json"); if (path == null) return;
            var report = new { inspectedAtUtc = DateTime.UtcNow, source = doc.SourcePath, format = doc.Format, bytes = doc.FileSize, doc.SupportsMembers, doc.HasDecodedRpf9Index, doc.Limitation, doc.Metadata, headerHex = Convert.ToHexString(doc.HeaderBytes), entries = doc.Entries.Count };
            await WriteExport(path, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }), default);
        });
        Hook(SourceInfo, () => { var doc = CurrentSource ?? documents.FirstOrDefault(); if (doc != null) DisplayText(doc.ArchiveLocation + "\n" + doc.Limitation + "\n" + string.Join("\n", doc.Metadata.Select(p => $"{p.Key}   {p.Value}"))); return Task.CompletedTask; });
        Hook(Hex, () => { if (Selected is { } r) DisplayText(Convert.ToHexString(r.Asset.Document.ReadPreview(r.Asset.Entry, 4096)).Chunk(32).Select(c => string.Join(' ', new string(c).Chunk(2).Select(v => new string(v)))).Aggregate("", (a, b) => a + b + "\n")); return Task.CompletedTask; });
        Hook(ReturnPreview, SelectAsset);
        Hook(ExtractResults, async () =>
        {
            var rows = matches.Where(a => a.Available).ToArray();
            if (rows.Length == 0) return;
            var dir = (await StorageProvider.OpenFolderPickerAsync(new() { Title = "Extract filtered results" })).FirstOrDefault()?.TryGetLocalPath();
            if (dir == null) return;
            await Busy(async token => { await Task.Run(() => WorkspaceAssetExporter.ExtractAsync(rows, LocalPath.Require(dir), token: token), token); Status.Text = $"Extracted {rows.Length:N0} results."; });
        });
        Hook(ExportShader, async () =>
        {
            if (Selected?.Asset is not { } a) return;
            var path = await Destination("Save shader listing", Path.GetFileNameWithoutExtension(a.Name) + ".txt"); if (path == null) return;
            await Busy(async t => { var listing = await Task.Run(() => RpfPreview.App.ShaderPreview.Describe(a.Document.ReadPreview(a.Entry, Rpf9ShaderRecovery.MaxShaderBytes, t)), t); await WriteExport(path, System.Text.Encoding.UTF8.GetBytes(listing), t); });
        });
        Hook(ExportTexture, async () =>
        {
            if (Selected?.Asset is not { } a) return;
            var path = await Destination("Export DDS", Path.GetFileNameWithoutExtension(a.Name) + ".dds"); if (path == null) return;
            int choice = (Choice.SelectedItem as ChoiceItem)?.Index ?? 0;
            var data = modelBytes; int boundary = split;
            await Busy(async t => { var bytes = await Task.Run(() => a.PoolTexture is { } pool ? TtpfReader.CreateDds(a.SourcePath, pool.Index, t) : data != null ? RageTextureDecoder.CreateDds(data.AsSpan(0, boundary), data.AsSpan(boundary), choice) : a.Document.ReadPreview(a.Entry, checked((int)a.Size), t), t); await WriteExport(path, bytes, t); });
        });
        Slice.ValueChanged += async (_, _) => { if (!updating) await RefreshSlice(); };
        WireEditing(); WireAssembly(); WirePlayback();
        KeyDown += async (_, e) =>
        {
            var mod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (!e.KeyModifiers.HasFlag(mod)) return;
            if (e.Key == Key.O) OpenFiles.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            else if (e.Key == Key.S) await Guard(() => SaveWorkspace(false)); else return;
            e.Handled = true;
        };
    }

    private void Hook(MenuItem item, Func<Task> action) => item.Click += async (_, _) => await Guard(action);
    private async Task Guard(Func<Task> action)
    {
        if (work != null) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private string? group;
    private sealed record Place(string Label, string? Folder = null, string? Group = null) { public override string ToString() => Label; }
    private void FillFolders(ArchiveDocument? doc)
    {
        bool old = updating; updating = true;
        try
        {
            var assets = index.Assets.Where(a => doc == null || a.Document == doc).ToArray();
            var places = new List<Place> { new("All content") };
            foreach (var family in assets.GroupBy(a => a.ContentGroup.Split(" / ")[0]).OrderBy(g => g.Key))
            {
                places.Add(new Place($"{family.Key} ({family.Count():N0})", Group: family.Key));
                places.AddRange(family.GroupBy(a => a.ContentGroup).Where(g => g.Key != family.Key).OrderBy(g => g.Key).Select(g => new Place($"    {g.Key} ({g.Count():N0})", Group: g.Key)));
            }
            places.AddRange(assets.Select(a => a.Entry.Folder).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Order().Select(p => new Place("Folder   " + p, p)));
            Folders.ItemsSource = places; Folders.SelectedItem = places.FirstOrDefault(p => p.Folder == folder && p.Group == group) ?? places[0];
        }
        finally { updating = old; }
    }
    private void BrowseDictionary(WorkspaceAsset asset)
    {
        updating = true; Sources.SelectedItem = Sources.Items.OfType<SourceItem>().First(i => i.Document == asset.Document); Search.Text = ""; updating = false;
        group = folder = null; container = asset.Entry.Path; FillFolders(asset.Document); _ = Filter();
    }
    private void UpdateToolStates()
    {
        var asset = Selected?.Asset;
        ExportShader.IsEnabled = asset?.Kind == "shader" || asset?.Entry.Extension is ".cso" or ".dxbc";
        ExportTexture.IsEnabled = asset?.PoolTexture?.CanExtract == true || textures.Count > 0 || asset?.Entry.Extension == ".dds";
        ExportAudio.IsEnabled = playback?.Wav != null && !playback.IsMovie;
        ExtractResults.IsEnabled = matches.Any(a => a.Available);
        SaveProject.IsEnabled = SaveAs.IsEnabled = documents.Count > 0;
        UpdateEditControls(); UpdateAssemblyControls();
    }
    private void DisplayText(string text) { Picture.IsVisible = false; Details.IsVisible = true; Details.Text = text; }
    private async Task<string?> Destination(string title, string name)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = title, SuggestedFileName = name, DefaultExtension = Path.GetExtension(name).TrimStart('.') });
        if (file?.TryGetLocalPath() is not { } chosen) return null;
        return SafeDestination(chosen);
    }
    private string SafeDestination(string chosen)
    {
        string path = LocalPath.Require(chosen);
        if (documents.Any(d => LocalPath.Comparer.Equals(d.SourcePath, path)) || LocalPath.Comparer.Equals(project?.ProjectPath, path) ||
            projects.Values.Any(p => LocalPath.Comparer.Equals(p.ProjectPath, path) || Path.GetRelativePath(p.AssetsDirectory, path).Split(Path.DirectorySeparatorChar)[0] != ".." && !Path.IsPathRooted(Path.GetRelativePath(p.AssetsDirectory, path))))
            throw new IOException("Choose an export destination outside the source and project files.");
        return path;
    }
    private async Task WriteExport(string path, byte[] bytes, CancellationToken token)
    {
        string temp = SafeDestination(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, token); token.ThrowIfCancellationRequested(); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
        Status.Text = "Saved " + Path.GetFileName(path);
    }
    private void ClearThumbnails()
    {
        thumbnails.Cancel(); thumbnails.Dispose(); thumbnails = new();
        foreach (var img in thumbnailImages) img.Dispose();
        thumbnailImages.Clear();
    }
    private async Task LoadThumbnails(ObservableCollection<Row> rows, CancellationToken token)
    {
        for (int i = 0; i < rows.Count && !token.IsCancellationRequested; i++)
        {
            var row = rows[i]; if (!row.ShowThumb || !row.Asset.Available) continue;
            try
            {
                var img = await Task.Run(() =>
                {
                    var a = row.Asset;
                    if (a.PoolTexture is { } pool) return Pixels(TtpfReader.DecodeTexturePreview(a.SourcePath, pool.Index, 96, token));
                    if (a.Texture is { } t)
                    {
                        var data = ArchiveReader.ReadResourceData(a.Document, a.Entry, token);
                        int n = a.Entry.ResourceSystemBytes ?? checked((int)a.Entry.Segments![0].Size);
                        int mip = 0; while (mip + 1 < t.MipCount && Math.Max(t.Width, t.Height) >> mip > 96) mip++;
                        return Pixels(RageTextureDecoder.DecodeTexturePixelsAtMip(data.AsSpan(0, n), data.AsSpan(n), t.Index, mip));
                    }
                    if (a.Size > 16 * 1024 * 1024) return null;
                    var bytes = a.Document.ReadPreview(a.Entry, (int)a.Size, token);
                    if (a.Entry.Extension == ".dds") return Pixels(DdsTextureReader.DecodeTexturePixels(bytes));
                    using var stream = new MemoryStream(bytes); return Bitmap.DecodeToWidth(stream, 96);
                }, token);
                if (img == null) continue;
                if (token.IsCancellationRequested) { img.Dispose(); return; }
                thumbnailImages.Add(img); row.SetThumbnail(img);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException) { }
        }
    }

    private void SetSlices(int count)
    {
        updating = true; Slice.Maximum = Math.Max(1, count); Slice.Value = 1; Slice.IsVisible = count > 1; Choices.IsVisible |= count > 1; updating = false;
    }
    private async Task RefreshSlice()
    {
        if (Selected?.Asset.PoolTexture is { } pool && preview is { } source)
        {
            int v = ++choiceVersion, slice = (int)(Slice.Value ?? 1) - 1; var a = Selected.Asset; var token = source.Token;
            try { var pixels = await Task.Run(() => TtpfReader.DecodeTexturePixels(a.SourcePath, pool.Index, slice, token), token); if (!token.IsCancellationRequested && v == choiceVersion) SetBitmap(Pixels(pixels)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (v == choiceVersion) Status.Text = ex.Message; }
        }
        else await ShowChoice(false);
    }
}
