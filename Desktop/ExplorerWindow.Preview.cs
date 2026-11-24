using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using RpfPreview.Core;
using System.Runtime.InteropServices;
using System.Text;

namespace RpfExplorer.Desktop;

public partial class ExplorerWindow
{
    private int choiceVersion;
    private IReadOnlyList<RageTextureInfo> textures = [];
    private sealed record ArrayLink(uint Hash, RageMetaArray Array)
    {
        public override string ToString() => $"0x{Hash:X8} → block {Array.TargetBlock}";
    }

    private void SetBitmap(Bitmap? next)
    {
        var old = bitmap; bitmap = next; Picture.Source = next; Picture.IsVisible = next != null;
        Details.IsVisible = next == null; old?.Dispose();
    }

    private static Bitmap Pixels(RageTexturePixels pixels)
    {
        var result = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var surface = result.Lock();
        int stride = pixels.Width * 4;
        for (int y = 0; y < pixels.Height; y++)
            Marshal.Copy(pixels.Bgra32, y * stride, surface.Address + y * surface.RowBytes, stride);
        return result;
    }

    private async Task SelectAsset()
    {
        preview?.Cancel(); preview?.Dispose(); preview = new CancellationTokenSource();
        var token = preview.Token;
        render?.Cancel(); choiceVersion++; modelView = null; modelBytes = metadata = null; models = []; textures = [];
        SetBitmap(null); updating = true;
        Choice.ItemsSource = null; Record.Value = 1; Links.ItemsSource = null; drag = null;
        Choices.IsVisible = Record.IsVisible = Follow.IsVisible = Links.IsVisible = ExportObj.IsVisible = false;
        updating = false; UpdateActions();
        if (Selected is not { } row) { Caption.Text = "Preview"; Details.Text = ""; return; }
        var asset = row.Asset; var doc = asset.Document; var e = asset.Entry;
        Caption.Text = row.Name; Details.Text = "Reading preview…";
        try
        {
            if (!asset.Available) { Details.Text = e.Limitation ?? "This member is unavailable."; return; }
            if (e.IsNestedArchive) { Details.Text = "Open this archive to browse its member table."; return; }
            if (asset.Kind is "model" or "dictionary" || asset.Texture != null)
            {
                var bytes = await Task.Run(() => ArchiveReader.ReadResourceData(doc, e, token), token);
                token.ThrowIfCancellationRequested();
                int boundary = e.ResourceSystemBytes ?? checked((int)e.Segments![0].Size);
                if (asset.Kind == "model")
                {
                    var decoded = await Task.Run<IReadOnlyList<RageModel>>(() =>
                    {
                        if (e.ResourceDrawableOffset == null) return RageModelDecoder.DecodeModels(bytes.AsSpan(0, boundary), bytes.AsSpan(boundary));
                        var model = RageModelDecoder.Decode(bytes.AsSpan(0, boundary), bytes.AsSpan(boundary), e.ResourceDrawableOffset);
                        return model == null ? [] : [model];
                    }, token);
                    token.ThrowIfCancellationRequested();
                    if (decoded.Count == 0) throw new InvalidDataException("No supported drawable was found.");
                    models = decoded; modelBytes = bytes; split = boundary;
                    SetChoices(models.Select((m, i) => new ChoiceItem(i, m.FragmentDrawableIndex switch { 0 => "Primary drawable", int n => $"Child drawable {n}", _ => m.Name })).ToArray(), 0);
                }
                else
                {
                    var dict = RageTextureDecoder.InspectMetadata(bytes.AsSpan(0, boundary), bytes.Length - boundary) ?? throw new InvalidDataException("Unsupported texture dictionary.");
                    if (dict.Textures.Count == 0) throw new InvalidDataException("Empty texture dictionary.");
                    textures = dict.Textures; modelBytes = bytes; split = boundary;
                    SetChoices(textures.Select((t, i) => new ChoiceItem(i, t.Name)).ToArray(), Math.Max(0, textures.ToList().FindIndex(t => t.Index == asset.Texture?.Index)));
                }
                await ShowChoice(true); return;
            }
            if (asset.PoolTexture is { } pool)
            {
                var pixels = await Task.Run(() => TtpfReader.DecodeTexturePreview(doc.SourcePath, pool.Index, 2048, token), token);
                token.ThrowIfCancellationRequested(); SetBitmap(Pixels(pixels)); return;
            }
            if (e.Compression == RageResourceMetadata.EntryKind)
            {
                var data = await Task.Run(() => RageResourceMetadata.ReadSystem(doc, e, token), token);
                var meta = RageResourceMetadata.Inspect(data) ?? throw new InvalidDataException("Invalid 0DRP directory.");
                token.ThrowIfCancellationRequested(); metadata = data;
                SetChoices(meta.Blocks.Select((b, i) => new ChoiceItem(i + 1, $"Block {i + 1}   0x{b.Type:X8}   {SizeText(b.Length)}{(i + 1 == meta.RootBlock ? "   root" : "")}")).ToArray(), meta.RootBlock - 1);
                await ShowChoice(true); return;
            }
            int limit = asset.IsImage && e.Size <= 16 * 1024 * 1024 ? 16 * 1024 * 1024 : 65536;
            var raw = await Task.Run(() => doc.ReadPreview(e, limit, token), token);
            token.ThrowIfCancellationRequested();
            if (asset.IsImage && raw.LongLength == e.Size)
            {
                if (e.Extension == ".dds") SetBitmap(Pixels(DdsTextureReader.DecodeTexturePixels(raw)));
                else { using var stream = new MemoryStream(raw); SetBitmap(Bitmap.DecodeToWidth(stream, 1024)); }
                return;
            }
            string head = $"{e.Path}\n{e.Compression}    {SizeText(e.Size)}\n";
            if (e.ResourceSystemBytes is int sys) head += $"System {sys:N0} bytes    Graphics {e.Size - sys:N0} bytes\n";
            bool text = raw.Take(4096).All(b => b >= 32 || b is 9 or 10 or 13);
            Details.Text = head + "\n" + (text ? Encoding.UTF8.GetString(raw) : string.Join("\n", raw.Take(512).Chunk(16).Select(line => BitConverter.ToString(line).Replace('-', ' '))));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { SetBitmap(null); Details.Text = ex.Message; } }
    }

    private void SetChoices(ChoiceItem[] items, int selected)
    {
        updating = true;
        try { Choice.ItemsSource = items; Choice.SelectedIndex = selected; Choices.IsVisible = items.Length > 1 || metadata != null; }
        finally { updating = false; }
    }

    private async Task ShowChoice(bool reset)
    {
        if (Choice.SelectedItem is not ChoiceItem option || Selected is not { } row || preview == null) return;
        int version = ++choiceVersion; var token = preview.Token; render?.Cancel(); modelView = null; ExportObj.IsVisible = false;
        try
        {
            if (metadata != null)
            {
                var bytes = metadata; int n = reset ? 1 : (int)(Record.Value ?? 1);
                var info = await Task.Run(() => RageResourceMetadata.InspectRecord(bytes, option.Index, n), token);
                if (token.IsCancellationRequested || version != choiceVersion) return;
                if (info == null) throw new InvalidDataException("The selected metadata record could not be validated.");
                updating = true;
                try
                {
                    Record.Maximum = info.RecordCount ?? 1; Record.Value = n; Record.IsVisible = info.RecordCount > 1;
                    var targets = info.Fields.Where(f => f.Array?.TargetBlock > 0).Select(f => new ArrayLink(f.NameHash, f.Array!)).ToArray();
                    Links.ItemsSource = targets; Links.SelectedIndex = targets.Length > 0 ? 0 : -1;
                    Follow.IsVisible = Links.IsVisible = targets.Length > 0;
                }
                finally { updating = false; }
                Details.Text = RageResourceMetadata.DescribeRecord(info); return;
            }
            if (modelBytes == null) return;
            var source = modelBytes; int boundary = split;
            if (textures.Count > 0)
            {
                var t = textures[option.Index];
                var pixels = await Task.Run(() => RageTextureDecoder.DecodeTexturePixels(source.AsSpan(0, boundary), source.AsSpan(boundary), t.Index), token);
                if (token.IsCancellationRequested || version != choiceVersion) return;
                SetBitmap(Pixels(pixels)); Status.Text = $"{t.Width} × {t.Height} · {t.Format}"; return;
            }
            var model = models[option.Index]; var assetIndex = index;
            var view = await Task.Run(() => ModelView.Load(row.Asset.Document, source, boundary, model, assetIndex, token), token);
            if (token.IsCancellationRequested || version != choiceVersion) return;
            modelView = view; yaw = -0.55f; pitch = 0.25f; zoom = 1; ExportObj.IsVisible = true;
            Status.Text = $"{model.Meshes.Count:N0} meshes · {model.Meshes.Sum(m => m.Indices.Count / 3):N0} triangles · drag to rotate, scroll to zoom";
            await RenderModel();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && version == choiceVersion) { SetBitmap(null); Details.Text = ex.Message; } }
    }

    private async Task RenderModel()
    {
        if (modelView is not { } view || preview == null) return;
        render?.Cancel();
        using var frame = CancellationTokenSource.CreateLinkedTokenSource(preview.Token);
        render = frame; float y = yaw, p = pitch, z = zoom;
        try
        {
            var pixels = await Task.Run(() => view.Render(y, p, z, frame.Token), frame.Token);
            if (!frame.IsCancellationRequested && render == frame) SetBitmap(Pixels(pixels));
        }
        catch (OperationCanceledException) { }
        finally { if (render == frame) render = null; }
    }

    private async Task SaveModel()
    {
        if (modelView?.Model is not { } model) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save displayed drawable", DefaultExtension = "obj", SuggestedFileName = "drawable.obj" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await Busy(async token =>
        {
            path = LocalPath.Require(path);
            if (documents.Any(d => LocalPath.Comparer.Equals(d.SourcePath, path))) throw new IOException("Choose a destination outside the source files.");
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(tmp, await Task.Run(() => RageModelDecoder.CreateObj(model), token), token); token.ThrowIfCancellationRequested(); File.Move(tmp, path, true); }
            finally { File.Delete(tmp); }
            Status.Text = "Saved " + Path.GetFileName(path);
        });
    }
}
