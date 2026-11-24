using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using RpfPreview.Core;

namespace RpfExplorer.Desktop;

public partial class ExplorerWindow
{
    private readonly List<WorkspaceAsset> parts = [];
    private WardrobeIndex? wardrobeIndex;
    private IReadOnlyList<VehicleWheelMount> mounts = [];
    private string assemblyDetails = "";
    private void WireAssembly()
    {
        Hook(AddParts, () => { var add = SelectedRows().Where(a => a.Kind == "model" && !parts.Contains(a)).ToArray(); if (parts.Count + add.Length > 64) throw new InvalidDataException("The queue holds 64 parts."); parts.AddRange(add); UpdateAssemblyControls(); Status.Text = $"{parts.Count} parts queued."; return Task.CompletedTask; });
        Hook(ClearParts, () => { parts.Clear(); UpdateAssemblyControls(); return Task.CompletedTask; });
        Hook(Together, () => Manual(SelectedRows())); Hook(Queued, () => Manual(parts.ToArray()));
        Hook(Vehicle, AssembleVehicle); QuickVehicle.Click += async (_, _) => await Guard(AssembleVehicle);
        Hook(AssemblyInfo, () => { DisplayText(assemblyDetails); return Task.CompletedTask; });
        Hook(ResetCam, async () => { yaw = -0.55f; pitch = 0.25f; zoom = 1; panX = panY = 0; await RenderModel(); });
        Hook(Wardrobe, ChooseWardrobe);
        QuickOutfit.Click += async (_, _) => await Guard(() => OpenWardrobe(WardrobeIndex.DrawableName(Selected!.Asset.Entry)));
    }
    private float panX, panY;
    private bool panning;
    private void UpdateAssemblyControls()
    {
        if (Queued == null) return;
        Queued.Header = $"Preview queued parts ({parts.Count})"; Queued.IsEnabled = ClearParts.IsEnabled = parts.Count > 0;
        var n = SelectedRows().Count(a => a.Available && a.Kind == "model"); AddParts.IsEnabled = n > 0; Together.IsEnabled = n > 1;
        Vehicle.IsEnabled = QuickVehicle.IsVisible = !showingAssembly && mounts.Count > 0 && modelView != null;
        QuickOutfit.IsVisible = !showingAssembly && Selected?.Asset is { Kind: "model" } a
            && a.ContentGroup.StartsWith("Characters /", StringComparison.Ordinal) && WardrobeIndex.DrawableName(a.Entry) != null;
        AssemblyInfo.IsEnabled = showingAssembly; ResetCam.IsEnabled = modelView != null;
    }
    private async Task Manual(WorkspaceAsset[] assets)
    {
        var rows = assets.Where(a => a.Available && a.Kind == "model").Take(65).ToArray();
        if (rows.Length is < 1 or > 64) throw new InvalidDataException("Choose 1 to 64 model parts.");
        await Assemble(b => b.Manual(rows));
    }
    private async Task AssembleVehicle()
    {
        if (modelView is not { } body || Selected?.Asset is not { } a || mounts.Count == 0) return;
        var pivots = mounts; var preset = ModelAssembly.Preset(a, body.Model.Name);
        await Assemble(b => b.Vehicle(body, pivots, preset));
    }
    private async Task Assemble(Func<ModelAssembly, ModelView> build)
    {
        await Busy(async token =>
        {
            var a = new ModelAssembly(index, TextureEdits(), token); var current = preview;
            Status.Text = "Assembling components…";
            var v = await Task.Run(() => build(a), token);
            if (current != preview) return;
            if (preview == null) preview = new();
            StopPlayback(); render?.Cancel(); choiceVersion++; showingAssembly = true; assemblyDetails = a.Details; modelView = v;
            Choices.IsVisible = false; ExportObj.IsVisible = true; Caption.Text = v.Model.Name; yaw = -0.55f; pitch = .25f; zoom = 1; panX = panY = 0;
            await RenderModel(); UpdateToolStates(); Status.Text = $"{v.Model.Meshes.Count:N0} meshes · {v.Model.Meshes.Sum(m => m.Indices.Count / 3):N0} triangles";
        });
    }
    private sealed record Kit(RageWardrobeCatalog Catalog, uint Id, string Source, string Names, int Found, int Total)
    { public override string ToString() => $"{Id:X8}   {Found}/{Total} models   {Names}"; }
    private Task ChooseWardrobe() => OpenWardrobe(null);
    private async Task OpenWardrobe(string? component)
    {
        WardrobeIndex? data = null;
        await Busy(async t => { var docs = documents.ToArray(); data = wardrobeIndex ??= await Task.Run(() => WardrobeIndex.Build(docs, t), t); });
        if (data == null) return;
        var sources = data.Catalogs.ToList();
        var names = index.Assets.Where(a => a.Kind == "model").ToLookup(a => WardrobeIndex.DrawableKey(a.Entry));
        var list = new ListBox(); var query = new TextBox { Watermark = "Search kit ID or component name" };
        var catalogs = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var choose = new Button { Content = "Assemble outfit" }; var import = new Button { Content = "Open extracted catalogue…" };
        var status = new TextBlock(); var stack = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel { Spacing = 6 }; top.Children.Add(catalogs); top.Children.Add(query); DockPanel.SetDock(top, Dock.Top); stack.Children.Add(top);
        var bottom = new StackPanel { Spacing = 6 }; bottom.Children.Add(status); bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { import, choose } }); DockPanel.SetDock(bottom, Dock.Bottom); stack.Children.Add(bottom); stack.Children.Add(list);
        var win = new Window { Title = "Assembled wardrobe", Width = 690, Height = 520, Content = stack }; Kit? chosen = null; Kit[] kits = [];
        void FilterKits() { var words = (query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries); var rows = kits.Where(k => words.All(w => k.ToString().Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray(); list.ItemsSource = rows; list.SelectedIndex = rows.Length > 0 ? 0 : -1; status.Text = $"{rows.Length:N0} matching kits"; }
        void LoadKits()
        {
            if (catalogs.SelectedIndex < 0) { kits = []; FilterKits(); return; }
            var s = sources[catalogs.SelectedIndex]; var result = new List<Kit>();
            foreach (var item in s.Catalog.Kits) { try { var res = s.Catalog.Resolve(item.Id); var keys = res.Selections.Select(x => x.DrawableKey).Where(k => k != 0).Distinct().ToArray(); var visible = keys.Select(k => names[k].FirstOrDefault() is { } part ? WardrobeIndex.DrawableName(part.Entry) : null).OfType<string>().ToArray(); result.Add(new(s.Catalog, item.Id, s.SourcePath, string.Join(" ", visible), visible.Length, keys.Length)); } catch (InvalidDataException) { } }
            kits = result.OrderByDescending(k => k.Found == k.Total && k.Total > 0).ThenBy(k => k.Id).ToArray(); FilterKits();
        }
        void SourcesChanged() { catalogs.ItemsSource = sources.Select(s => Path.GetFileName(s.SourcePath) + $" @ {s.Offset:X}").ToArray(); catalogs.SelectedIndex = sources.Count - 1; }
        catalogs.SelectionChanged += (_, _) => LoadKits(); query.TextChanged += (_, _) => FilterKits();
        choose.Click += (_, _) => { if (list.SelectedItem is Kit k) { chosen = k; win.Close(); } };
        import.Click += async (_, _) => { try { var file = (await win.StorageProvider.OpenFilePickerAsync(new() { Title = "Open wardrobe catalogue" })).FirstOrDefault()?.TryGetLocalPath(); if (file == null) return; if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("Catalogue exceeds 64 MiB."); var cat = RageWardrobeCatalog.Decode(await File.ReadAllBytesAsync(file)) ?? throw new InvalidDataException("Unrecognized wardrobe catalogue."); sources.Add(new(file, 0, cat)); SourcesChanged(); } catch (Exception ex) { status.Text = ex.Message; } };
        SourcesChanged();
        query.Text = component;
        if (sources.Count == 0) status.Text = "Open a catalogue or scan the loaded archives for wardrobe metadata.";
        await win.ShowDialog(this); if (chosen != null) await Assemble(b => b.Outfit(chosen.Catalog, chosen.Id, data));
    }
}
