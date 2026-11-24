using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RpfPreview.Core;

namespace RpfExplorer.Desktop;

public partial class ExplorerWindow
{
    private AssetWorkspaceProject? project;
    private readonly Dictionary<string, ReplacementWorkspace> projects = new(LocalPath.Comparer);
    private readonly HashSet<(string Source, string Entry, long Offset)> edited = [];
    private bool showingAssembly;
    private void WireEditing()
    {
        Hook(OpenProject, async () => { if ((await StorageProvider.OpenFilePickerAsync(new() { Title = "Open workspace or replacement project" })).FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadProject(path); });
        Hook(SaveProject, () => SaveWorkspace(false)); Hook(SaveAs, () => SaveWorkspace(true));
        Hook(Stage, StageFile); QuickStage.Click += async (_, _) => await Guard(StageFile);
        Hook(Original, async () => { int choice = Choice.SelectedIndex; await SelectAsset(); if (choice >= 0 && Choice.Items.Count > choice) Choice.SelectedIndex = choice; });
        Hook(Staged, ShowStaged);
        Hook(RemoveEdit, async () =>
        {
            if (Selected?.Asset is { } a && Target() is { } target && projects.TryGetValue(a.SourcePath, out var p) && p.Remove(target))
            { RefreshEdits(); await SelectAsset(); Status.Text = "Removed staged replacement."; }
        });
        Hook(BuildNative, () => ExportNative(false)); Hook(PoolCopy, () => ExportNative(true));
    }
    private bool IsEdited(WorkspaceAsset a) => edited.Contains((LocalPath.Identity(a.SourcePath), a.Entry.Path, a.Entry.Offset));
    private void RefreshEdits()
    {
        edited.Clear();
        foreach (var p in projects.Values)
            foreach (var record in p.Entries) edited.Add((LocalPath.Identity(p.SourcePath), record.Target.EntryPath, record.Target.EntryOffset));
        foreach (var row in Assets.Items.OfType<Row>()) row.MarkEdited(IsEdited(row.Asset));
        UpdateEditControls();
    }
    private WorkspaceViewState ViewState()
    {
        var doc = (Sources.SelectedItem as SourceItem)?.Document;
        var nested = doc?.ParentArchive != null ? doc : Selected?.Asset.Document.ParentArchive != null ? Selected.Asset.Document : null;
        return new(Search.Text ?? "", Type.SelectedItem as string ?? "all", Gallery.IsChecked == true, Only.IsChecked == true, doc?.SourcePath, folder, container, Selected?.Asset.SourcePath, Selected?.Asset.Entry.Path, Selected?.Asset.SubresourceIndex ?? -1, GlobalSearch.IsChecked == true)
        { NestedSource = nested?.SourcePath, NestedSlots = nested?.NestedArchiveSlots.ToArray(), ContentGroup = group, RecoverySources = documents.Where(d => d.Format == "RPF9" && !d.HasDecodedRpf9Index).Select(d => d.SourcePath).ToArray() };
    }
    private async Task SaveWorkspace(bool copy)
    {
        if (documents.Count == 0) return;
        if (project == null || copy)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Create workspace", SuggestedFileName = "workspace.rpfworkspace", DefaultExtension = "rpfworkspace" });
            if (file?.TryGetLocalPath() is not { } path) return;
            var created = AssetWorkspaceProject.Create(path, documents.Select(d => d.SourcePath));
            foreach (var previous in projects.Values) created.ImportSourceProject(previous);
            created.Save(ViewState()); project = created; projects.Clear();
            foreach (var src in created.SourcePaths) if (created.ForSource(src, false) is { } p) projects[src] = p;
        }
        else project.Save(ViewState());
        RefreshEdits(); Status.Text = "Saved " + Path.GetFileName(project.ProjectPath);
    }
    private async Task LoadProject(string path)
    {
        await Busy(async token =>
        {
            AssetWorkspaceProject? next = null; ReplacementWorkspace? legacy = null;
            if (Path.GetExtension(path).Equals(".rpfproject", StringComparison.OrdinalIgnoreCase)) legacy = ReplacementWorkspace.Load(path);
            else next = AssetWorkspaceProject.Load(path);
            var view = next?.View ?? new WorkspaceViewState(); var paths = next?.SourcePaths ?? [legacy!.SourcePath];
            var docs = await Task.Run(() => paths.Select(s =>
            {
                var nested = view.NestedSlots != null && LocalPath.Comparer.Equals(view.NestedSource, s);
                var provider = !nested && view.RecoverySources?.Contains(s, LocalPath.Comparer) == true ? null : decoder;
                ArchiveDocument d;
                try { d = ArchiveReader.Open(s, provider, token); }
                catch (Exception ex) when (!nested && provider != null && ex is IOException or InvalidDataException or NotSupportedException)
                { d = ArchiveReader.Open(s, token: token); }
                if (view.NestedSlots != null && LocalPath.Comparer.Equals(view.NestedSource, s))
                    foreach (int slot in view.NestedSlots) d = ArchiveReader.OpenNestedRpf9(d, d.Entries.Single(e => e.Index == slot && e.IsNestedArchive), token);
                return d;
            }).ToList(), token);
            var built = await Task.Run(() => AssetWorkspaceIndex.Build(docs, cancellationToken: token), token);
            token.ThrowIfCancellationRequested(); project = next; projects.Clear(); parts.Clear();
            if (legacy != null) projects[legacy.SourcePath] = legacy;
            if (next != null) foreach (var s in paths) if (next.ForSource(s, false) is { } p) projects[s] = p;
            RefreshEdits();
            var scoped = docs.FirstOrDefault(d => LocalPath.Comparer.Equals(d.SourcePath, view.ScopeSource));
            await SetWorkspace(docs, built, scoped);
            updating = true;
            try { Type.SelectedItem = view.Type; Gallery.IsChecked = view.Thumbnails; Only.IsChecked = view.AvailableOnly; GlobalSearch.IsChecked = view.SearchAllSources; Search.Text = view.Search; folder = view.Folder; container = view.Container; group = view.ContentGroup; FillFolders(scoped); }
            finally { updating = false; }
            searchDelay.Stop(); await Filter();
            var first = legacy?.Entries.FirstOrDefault()?.Target;
            int pos = Array.FindIndex(matches, a => LocalPath.Comparer.Equals(a.SourcePath, view.SelectedSource ?? legacy?.SourcePath) && a.Entry.Path == (view.SelectedEntry ?? first?.EntryPath) && (view.SelectedTexture < 0 || a.SubresourceIndex == view.SelectedTexture));
            if (pos >= 0) { page = pos / PageSize; ShowPage(); Assets.SelectedIndex = pos % PageSize; }
            Status.Text = "Opened " + Path.GetFileName(path);
        });
    }
    private ReplacementTarget? Target()
    {
        if (showingAssembly || Selected?.Asset is not { } a || !a.Available) return null;
        var e = a.Entry; int choice = (Choice.SelectedItem as ChoiceItem)?.Index ?? 0;
        if (a.PoolTexture is { } p) return new(e.Path, e.Offset, "texture", p.Index, p.AssetKey.ToString("X16"));
        if (a.Kind == "dictionary" || a.Texture != null)
        {
            var t = textures.FirstOrDefault(t => t.Index == choice) ?? a.Texture;
            return t == null ? null : new(e.Path, e.Offset, "texture", t.Index, t.AssetKey?.ToString("X16"));
        }
        return a.Kind switch { "model" => new(e.Path, e.Offset, "model"), "audio" => new(e.Path, e.Offset, "audio", choice), _ => new(e.Path, e.Offset, "binary") };
    }
    private (ReplacementWorkspace Project, ReplacementRecord Record)? CurrentEdit()
    {
        if (Selected?.Asset is { } a && Target() is { } t && projects.TryGetValue(a.SourcePath, out var p) && p.Find(t) is { } record) return (p, record);
        return null;
    }
    private void UpdateEditControls()
    {
        Stage.IsEnabled = QuickStage.IsEnabled = Target() != null;
        var edit = CurrentEdit(); Staged.IsEnabled = Original.IsEnabled = RemoveEdit.IsEnabled = edit != null;
        bool dds = edit?.Record.Format == "DDS";
        BuildNative.IsEnabled = dds && textures.Count > 0;
        PoolCopy.IsEnabled = dds && Selected?.Asset.PoolTexture?.SupportsNativeReplacement == true;
    }
    private async Task StageFile()
    {
        if (Selected?.Asset is not { } a || Target() is not { } t) return;
        var chosen = await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose replacement for " + t.Kind });
        if (chosen.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (!projects.TryGetValue(a.SourcePath, out var p))
        {
            if (project == null) await SaveWorkspace(false);
            if (project == null) return;
            projects[a.SourcePath] = p = project.ForSource(a.SourcePath, true)!;
        }
        await ImportEdit(a, t, p, path);
        if (p.Find(t) != null && Selected?.Asset == a) await ShowStaged();
    }
    private async Task ImportEdit(WorkspaceAsset asset, ReplacementTarget target, ReplacementWorkspace source, string path)
    {
        await Busy(async token =>
        {
            await Task.Run(() => source.Import(target, path, "Staged " + target.Kind), token);
            project?.Save(ViewState()); RefreshEdits();

        });
    }
    private async Task ShowStaged()
    {
        if (CurrentEdit() is not { } edit) return;
        string path = edit.Project.AssetPath(edit.Record);
        preview?.Cancel(); preview = new(); var token = preview.Token; StopPlayback(); modelView = null;
        var target = edit.Record.Target;
        switch (target.Kind)
        {
            case "model":
                if (edit.Record.Length > ObjModelReader.MaxCharacters * 4L) throw new InvalidDataException("OBJ exceeds the preview limit.");
                var imported = await Task.Run(() => ObjModelReader.Parse(File.ReadAllText(path), edit.Record.OriginalFileName), token);
                token.ThrowIfCancellationRequested(); modelView = new(imported, new Dictionary<int, RageTexturePixels>()); ExportObj.IsVisible = true; await RenderModel(); break;
            case "texture":
                SetBitmap(edit.Record.Format == "DDS" ? Pixels(DdsTextureReader.DecodeTexturePixels(await File.ReadAllBytesAsync(path, token))) : new Bitmap(path)); break;
            case "audio": await OpenWave(path, token); break;
            default:
                using (var f = File.OpenRead(path)) { var b = new byte[(int)Math.Min(f.Length, 1024)]; f.ReadExactly(b); DisplayText(Convert.ToHexString(b)); }
                break;
        }
        Status.Text = "Staged replacement · " + edit.Record.OriginalFileName;
    }
    private async Task ExportNative(bool pool)
    {
        if (CurrentEdit() is not { } edit || Selected?.Asset is not { } a) return;
        string input = edit.Project.AssetPath(edit.Record); int n = edit.Record.Target.SubresourceIndex;
        var path = await Destination(pool ? "Export modified pool copy" : "Save decoded texture resource", pool ? "modified.ttpf" : "modified.r9texdata"); if (path == null) return;
        await Busy(async token =>
        {
            var dds = await File.ReadAllBytesAsync(input, token);
            if (pool) { var r = await Task.Run(() => Rds5TextureWriter.ExportReplacementCopy(a.SourcePath, n, dds, path, token), token); Status.Text = r.ValidationNote; }
            else { var result = await Task.Run(() => { var d = ArchiveReader.ReadResourceData(a.Document, a.Entry, token); int b = a.Entry.ResourceSystemBytes ?? checked((int)a.Entry.Segments![0].Size); return NativeTextureReplacement.Stage(d.AsSpan(0, b), d.AsSpan(b), n, dds, token); }, token); await WriteExport(path, result.ResourceData, token); }
        });
    }
    private Dictionary<ulong, string> TextureEdits()
    {
        var map = new Dictionary<ulong, string>();
        foreach (var p in projects.Values) foreach (var r in p.Entries)
            if (r.Target.Kind == "texture" && ulong.TryParse(r.Target.AssetKey, System.Globalization.NumberStyles.HexNumber, null, out ulong key)) map.TryAdd(key, p.AssetPath(r));
        return map;
    }
}
