using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using RpfPreview.Core;
using System.Text;

namespace RpfExplorer.Desktop;

public partial class ExplorerWindow : Window
{
    private sealed record Row(WorkspaceAsset Asset)
    {
        public string Name => Asset.Texture?.Name ?? (Asset.Entry.IsNameCandidate ? Asset.Name : Asset.Entry.EmbeddedModelNames.FirstOrDefault() ?? Asset.Name);
        public string Location => $"{Asset.Kind} · {Asset.Document.ArchiveLocation}";
        public string Size => SizeText(Asset.Size);
    }
    private sealed record SourceItem(ArchiveDocument? Document)
    {
        public override string ToString() => Document?.ArchiveLocation ?? "All sources";
    }
    private sealed record ChoiceItem(int Index, string Label)
    {
        public override string ToString() => Label;
    }
    private const int PageSize = 250;
    private readonly List<ArchiveDocument> documents = [];
    private AssetWorkspaceIndex index = AssetWorkspaceIndex.Build([]);
    private WorkspaceAsset[] matches = [];
    private int page, filterVersion;
    private bool updating;
    private CancellationTokenSource? work, preview, render;
    private Rpf9IndexProvider? decoder;
    private byte[]? metadata, modelBytes;
    private int split;
    private IReadOnlyList<RageModel> models = [];
    private ModelView? modelView;
    private Bitmap? bitmap;
    private float yaw = -0.55f, pitch = 0.25f, zoom = 1;
    private Point? drag;
    private Row? Selected => Assets.SelectedItem as Row;
    private ArchiveDocument? CurrentSource => (Sources.SelectedItem as SourceItem)?.Document ?? Selected?.Asset.Document;
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(170) };

    public ExplorerWindow() : this([]) { }

    public ExplorerWindow(string[] args)
    {
        InitializeComponent();
        Sources.ItemsSource = new[] { new SourceItem(null) }; Sources.SelectedIndex = 0;
        OpenFiles.Click += async (_, _) => { var files = await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = true, Title = "Open archives or resources" }); await OpenPaths(files.Select(f => f.TryGetLocalPath()).OfType<string>()); };
        OpenDirectory.Click += async (_, _) => { var dirs = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Open archive folder" }); await OpenPaths(dirs.Select(f => f.TryGetLocalPath()).OfType<string>()); };
        Configure.Click += async (_, _) => await ConfigureDecoder();
        Cancel.Click += (_, _) => work?.Cancel();
        Extract.Click += async (_, _) => await ExtractSelected();
        ExportObj.Click += async (_, _) => await SaveModel();
        Enter.Click += async (_, _) => await Navigate(false);
        Assets.DoubleTapped += async (_, _) => { if (Selected?.Asset.Entry.IsNestedArchive == true) await Navigate(false); };
        Up.Click += async (_, _) => await Navigate(true);
        Scan.Click += async (_, _) => await ScanSource();
        Dark.IsCheckedChanged += (_, _) => RequestedThemeVariant = Dark.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
        Search.TextChanged += (_, _) => { searchDelay.Stop(); searchDelay.Start(); };
        searchDelay.Tick += async (_, _) => { searchDelay.Stop(); await Filter(); };
        Sources.SelectionChanged += async (_, _) => { if (!updating) await Filter(); };
        Sort.SelectionChanged += async (_, _) => { if (!updating) await Filter(); };
        Prev.Click += (_, _) => { page--; ShowPage(); };
        Next.Click += (_, _) => { page++; ShowPage(); };
        Assets.SelectionChanged += async (_, _) => await SelectAsset();
        Choice.SelectionChanged += async (_, _) => { if (!updating) await ShowChoice(true); };
        Record.ValueChanged += async (_, _) => { if (!updating && metadata != null) await ShowChoice(false); };
        Follow.Click += async (_, _) =>
        {
            if (Links.SelectedItem is not ArrayLink link || metadata == null) return;
            var target = RageResourceMetadata.InspectRecord(metadata, link.Array.TargetBlock);
            if (target == null) return;
            int n = target.RecordCount > 0 && target.Length > 0 ? 1 + link.Array.TargetOffset / target.Length : 1;
            updating = true;
            try { Choice.SelectedIndex = link.Array.TargetBlock - 1; Record.Maximum = target.RecordCount ?? 1; Record.Value = Math.Min(n, (int)Record.Maximum); }
            finally { updating = false; }
            await ShowChoice(false);
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is { } files) await OpenPaths(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        });
        Picture.PointerPressed += (_, e) => { if (modelView != null) { drag = e.GetPosition(Picture); e.Pointer.Capture(Picture); } };
        Picture.PointerReleased += (_, e) => { drag = null; e.Pointer.Capture(null); };
        Picture.PointerMoved += async (_, e) =>
        {
            if (drag is not { } last || modelView == null) return;
            var point = e.GetPosition(Picture); drag = point;
            yaw += (float)(point.X - last.X) * 0.012f; pitch = Math.Clamp(pitch + (float)(point.Y - last.Y) * 0.012f, -1.55f, 1.55f);
            await RenderModel();
        };
        Picture.PointerWheelChanged += async (_, e) =>
        {
            if (modelView == null) return;
            zoom = Math.Clamp(zoom * (float)Math.Pow(1.15, e.Delta.Y), 0.2f, 5); e.Handled = true; await RenderModel();
        };
        Opened += async (_, _) =>
        {
            try { if (LocalPath.FileExists(Rpf9IndexProvider.DefaultConfigurationPath)) decoder = Rpf9IndexProvider.Load(Rpf9IndexProvider.DefaultConfigurationPath); }
            catch (Exception ex) { Status.Text = ex.Message; }
            await OpenPaths(args);
        };
        Closed += (_, _) => { searchDelay.Stop(); filterVersion++; work?.Cancel(); preview?.Cancel(); render?.Cancel(); SetBitmap(null); };
    }

    private async Task Busy(Func<CancellationToken, Task> action)
    {
        if (work != null) return;
        using var source = new CancellationTokenSource();
        work = source; Cancel.IsVisible = true;
        OpenFiles.IsEnabled = OpenDirectory.IsEnabled = Configure.IsEnabled = false;
        try { await action(source.Token); }
        catch (OperationCanceledException) { Status.Text = "Canceled."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { work = null; Cancel.IsVisible = false; OpenFiles.IsEnabled = OpenDirectory.IsEnabled = Configure.IsEnabled = true; }
    }

    private async Task OpenPaths(IEnumerable<string> paths)
    {
        var incoming = paths.ToArray();
        if (incoming.Length == 0) return;
        await Busy(async token =>
        {
            Status.Text = "Opening sources…";
            var result = await Task.Run(() =>
            {
                var next = documents.ToList();
                var seen = next.Select(d => d.SourcePath).ToHashSet(LocalPath.Comparer);
                var notices = new List<string>();
                foreach (string path in incoming.Select(LocalPath.Require))
                {
                    IEnumerable<string> files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, AttributesToSkip = LocalPath.RemoteAttributes }).Where(ArchiveReader.IsWorkspaceSource) : [path];
                    foreach (string file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!seen.Add(file)) continue;
                        if (next.Count >= 512) throw new InvalidDataException("A workspace can hold up to 512 sources.");
                        try { next.Add(ArchiveReader.Open(file, decoder, token)); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                        {
                            if (decoder != null && Path.GetExtension(file).Equals(".rpf", StringComparison.OrdinalIgnoreCase))
                            {
                                try { next.Add(ArchiveReader.Open(file, token: token)); notices.Add($"Recovery mode: {ex.Message}"); continue; }
                                catch (Exception fallback) when (fallback is IOException or InvalidDataException or NotSupportedException) { }
                            }
                            notices.Add($"{Path.GetFileName(file)}: {ex.Message}");
                        }
                    }
                }
                return (next, built: AssetWorkspaceIndex.Build(next, cancellationToken: token), notices);
            }, token);
            token.ThrowIfCancellationRequested();
            await SetWorkspace(result.next, result.built, null);
            Status.Text = result.notices.FirstOrDefault() ?? $"{documents.Count:N0} sources · {index.Assets.Count:N0} assets";
        });
    }

    private async Task SetWorkspace(List<ArchiveDocument> next, AssetWorkspaceIndex built, ArchiveDocument? current)
    {
        documents.Clear(); documents.AddRange(next); index = built;
        updating = true;
        try
        {
            var items = new[] { new SourceItem(null) }.Concat(documents.Select(d => new SourceItem(d))).ToArray();
            Sources.ItemsSource = items; Sources.SelectedItem = items.First(i => i.Document == current);
            Search.Text = ""; Title = documents.Count == 1 ? $"{Path.GetFileName(documents[0].SourcePath)} · rpf9-explorer" : "rpf9-explorer";
        }
        finally { updating = false; }
        await Filter();
    }

    private async Task Filter()
    {
        int version = ++filterVersion;
        var query = AssetSearchQuery.Parse(Search.Text ?? "");
        var source = (Sources.SelectedItem as SourceItem)?.Document;
        var currentIndex = index; int sort = Sort.SelectedIndex;
        var list = await Task.Run(() =>
        {
            var subset = currentIndex.Assets.Where(a => (source == null || a.Document == source) && query.Matches(a));
            return (sort switch { 1 => subset.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase), 2 => subset.OrderByDescending(a => a.Size), 3 => subset.OrderBy(a => a.Size), _ => subset }).ToArray();
        });
        if (version != filterVersion) return;
        matches = list; page = 0; ShowPage();
    }

    private void ShowPage()
    {
        Assets.ItemsSource = matches.Skip(page * PageSize).Take(PageSize).Select(a => new Row(a)).ToArray();
        Count.Text = matches.Length == 0 ? "0 matches" : $"{page * PageSize + 1:N0}–{Math.Min((page + 1) * PageSize, matches.Length):N0} of {matches.Length:N0}";
        Prev.IsEnabled = page > 0; Next.IsEnabled = (page + 1) * PageSize < matches.Length;
        UpdateActions();
        if (matches.Length == 1) Assets.SelectedIndex = 0;
    }

    private void UpdateActions()
    {
        Extract.IsEnabled = Selected?.Asset.Available == true;
        Enter.IsVisible = Selected?.Asset.Entry.IsNestedArchive == true;
        Up.IsEnabled = CurrentSource?.ParentArchive != null;
        Scan.IsEnabled = (CurrentSource ?? (documents.Count == 1 ? documents[0] : null))?.Format == "RPF9";
    }

    private async Task Navigate(bool parent)
    {
        if (CurrentSource is not { } original) return;
        var entry = Selected?.Asset.Entry;
        if (parent ? original.ParentArchive == null : entry?.IsNestedArchive != true) return;
        await Busy(async token =>
        {
            Status.Text = "Reading archive index…";
            var doc = parent ? original.ParentArchive! : await Task.Run(() => ArchiveReader.OpenNestedRpf9(original, entry!, token), token);
            await ReplaceDocument(original, doc, token);
            Status.Text = doc.ArchiveLocation;
        });
    }

    private async Task ReplaceDocument(ArchiveDocument previous, ArchiveDocument replacement, CancellationToken token)
    {
        var next = documents.Select(d => d == previous ? replacement : d).ToList();
        var built = await Task.Run(() => AssetWorkspaceIndex.Build(next, cancellationToken: token), token);
        token.ThrowIfCancellationRequested(); await SetWorkspace(next, built, replacement);
    }

    private async Task ScanSource()
    {
        var doc = CurrentSource ?? (documents.Count == 1 ? documents[0] : null);
        if (doc?.Format != "RPF9") return;
        await Busy(async token =>
        {
            var progress = new Progress<PayloadIndexProgress>(p => Status.Text = p.Message);
            await Task.Run(() => PayloadIndexBuilder.Build(doc.SourcePath, progress, token), token);
            var replacement = await Task.Run(() => ArchiveReader.Open(doc.SourcePath, token: token), token);
            await ReplaceDocument(doc, replacement, token);
            Status.Text = $"Scan complete · {replacement.Entries.Count:N0} entries";
        });
    }

    private async Task ConfigureDecoder()
    {
        var chosen = await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose a local decoder configuration" });
        if (chosen.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        await Busy(async token =>
        {
            var provider = Rpf9IndexProvider.Load(path);
            string target = LocalPath.Require(Rpf9IndexProvider.DefaultConfigurationPath);
            if (!LocalPath.Comparer.Equals(target, Path.GetFullPath(path)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(path, temp); File.Move(temp, target, true); } finally { File.Delete(temp); }
            }
            decoder = provider;
            if (documents.Count == 0) { Status.Text = "Decoder configured."; return; }
            var next = await Task.Run(() => documents.Select(d => ArchiveReader.Open(d.SourcePath, provider, token)).ToList(), token);
            var built = await Task.Run(() => AssetWorkspaceIndex.Build(next, cancellationToken: token), token);
            await SetWorkspace(next, built, null); Status.Text = "Reopened sources with the configured decoder.";
        });
    }

    private async Task ExtractSelected()
    {
        if (Selected?.Asset is not { Available: true } asset) return;
        var dirs = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Extract selected asset" });
        if (dirs.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        await Busy(async token => { await Task.Run(() => WorkspaceAssetExporter.ExtractAsync([asset], LocalPath.Require(folder), token: token), token); Status.Text = "Extracted " + asset.Name; });
    }

    private static string SizeText(long n) => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024d:N1} KiB" : $"{n / 1048576d:N1} MiB";
}
