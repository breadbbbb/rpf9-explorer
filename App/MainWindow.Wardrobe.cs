using Microsoft.Win32;
using RpfPreview.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RpfPreview.App;

public partial class MainWindow
{
    private sealed record WardrobeCatalogChoice(string Label, RageWardrobeCatalog Catalog);
    private sealed record WardrobeKitChoice(uint Id, string Label, int Matches, int Total, string Search);
    private sealed record WardrobeModelOption(WorkspaceAsset Asset, RageModel Model, int Lod, int Triangles);
    private Window? _wardrobeWindow;
    private readonly Dictionary<(string Source, string Path, long Offset, int? Drawable), ulong> _wardrobeAlbedos = new();
    private readonly Dictionary<(string Source, string Path, long Offset, int? Drawable), (uint Palette, byte Index)> _wardrobeTints = new();

    private ulong? WardrobeAlbedoKey(ArchiveDocument document, ArchiveEntry entry)
        => _wardrobeAlbedos.TryGetValue((document.SourcePath, entry.Path, entry.Offset, entry.ResourceDrawableOffset), out ulong key) ? key : null;

    private void OpenWardrobe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_wardrobeWindow is { } existing) { existing.Activate(); return; }
        var lifetime = new CancellationTokenSource();
        var cancellation = lifetime.Token;
        var window = _wardrobeWindow = new Window { Owner = this, Title = "Assembled wardrobe", Width = 820, Height = 580, MinWidth = 600, MinHeight = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(BackgroundProperty, "AppSurface"); window.SetResourceReference(ForegroundProperty, "AppText");
        var root = new DockPanel { Margin = new Thickness(18) }; window.Content = root;
        var heading = new TextBlock { Text = "Assemble an authored outfit", FontSize = 19, Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var explanation = new TextBlock { Text = "Outfit kits select native model and texture IDs. Indexed matches are checked again when the outfit is assembled; unfinished download regions may still be unavailable.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(explanation, Dock.Top); root.Children.Add(explanation);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var assemble = new Button { Content = "Assemble outfit", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", IsCancel = true }; footer.Children.Add(assemble); footer.Children.Add(close); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var catalogues = new ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 5, 0, 8) };
        var search = new TextBox { Margin = new Thickness(0, 5, 0, 8), ToolTip = "Filter by outfit kit ID or indexed native component names. Separate terms with spaces to require each term." };
        if (ReferenceEquals(sender, MatchingOutfitsButton) && FilesGrid.SelectedItem is FileRow row && WardrobeIndex.DrawableName(row.Entry) is { } native)
            search.Text = native;
        var kits = new ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 5, 0, 12), IsEnabled = false, MaxDropDownHeight = 260 };
        var browse = new Button { Content = "Open catalogue file…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        var details = new TextBlock { Text = "Looking for wardrobe catalogues in loaded sources…", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(new TextBlock { Text = "Catalogue" }); panel.Children.Add(catalogues); panel.Children.Add(browse); panel.Children.Add(new TextBlock { Text = "Find kit ID or indexed component" }); panel.Children.Add(search); panel.Children.Add(new TextBlock { Text = "Outfit kit" }); panel.Children.Add(kits); panel.Children.Add(details);
        var assets = (_assetIndex?.Assets ?? []).Where(a => a.Kind == "model" && a.Available).ToArray();
        var lookup = BuildWardrobeLookup(assets); var choices = new List<WardrobeCatalogChoice>(); var kitRows = new List<WardrobeKitChoice>();

        void FilterKits()
        {
            uint? previous = (kits.SelectedItem as WardrobeKitChoice)?.Id;
            string[] terms = search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var visible = kitRows.Where(row => terms.All(term => row.Search.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            kits.ItemsSource = visible; kits.IsEnabled = visible.Length > 0;
            if (visible.Length > 0) kits.SelectedItem = visible.FirstOrDefault(row => row.Id == previous) ?? visible[0];
            else { assemble.IsEnabled = false; details.Text = "No outfit kits match these terms. Search uses native indexed component names and exact kit IDs."; }
        }

        void ShowKits()
        {
            kits.ItemsSource = null; assemble.IsEnabled = false;
            if (catalogues.SelectedItem is not WardrobeCatalogChoice choice) return;
            var rows = new List<WardrobeKitChoice>();
            foreach (var kit in choice.Catalog.Kits)
            {
                var result = choice.Catalog.Resolve(kit.Id);
                var keys = result.Selections.Where(s => s.DrawableKey != 0).Select(s => s.DrawableKey).Distinct().ToArray();
                int matches = keys.Count(lookup.ContainsKey);
                var names = keys.Where(lookup.ContainsKey).SelectMany(key => lookup[key]).Select(asset =>
                    WardrobeIndex.DrawableName(asset.Entry) ?? "").Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                var heads = names.Where(name => (name.StartsWith("head_", StringComparison.OrdinalIgnoreCase) || name.Contains("_head_", StringComparison.OrdinalIgnoreCase)) && !name.Contains("_eyes_", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                string label = $"{kit.Id:X8} · {matches} / {keys.Length} model IDs indexed" + (heads.Length == 1 ? " · indexed " + heads[0] : "") + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} warnings" : "");
                rows.Add(new(kit.Id, label, matches, keys.Length, $"{kit.Id:X8} 0x{kit.Id:X8} " + string.Join(' ', names)));
            }
            kitRows = rows.OrderByDescending(r => r.Total > 0 && r.Matches == r.Total).ThenByDescending(r => r.Matches).ThenBy(r => r.Id).ToList();
            FilterKits();
        }
        search.TextChanged += (_, _) => FilterKits();
        catalogues.SelectionChanged += (_, _) => { try { ShowKits(); } catch (InvalidDataException ex) { details.Text = ex.Message; } };
        kits.SelectionChanged += (_, _) =>
        {
            assemble.IsEnabled = kits.SelectedItem is WardrobeKitChoice { Matches: > 0, Total: > 0 };
            if (kits.SelectedItem is WardrobeKitChoice kit) details.Text = $"Kit {kit.Id:X8}: {kit.Matches} of {kit.Total} drawable IDs have indexed matches. Models are validated on assembly and shown in their stored pose. Appearance overrides require supported texture data.";
        };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Open a wardrobe catalogue", Filter = "Wardrobe metadata|*.psin;*.pso;*.0drp|All files|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(window) != true) return;
            try
            {
                var file = new FileInfo(LocalPath.Require(dialog.FileName));
                if (file.Length is < 96 or > RageWardrobeCatalog.MaxBytes) throw new InvalidDataException("The catalogue file exceeds the supported size.");
                var parsed = RageWardrobeCatalog.Decode(File.ReadAllBytes(file.FullName)) ?? throw new InvalidDataException("This is not a complete, supported wardrobe catalogue.");
                choices.Add(new(file.Name, parsed)); catalogues.ItemsSource = choices.ToArray(); catalogues.SelectedIndex = choices.Count - 1;
            }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { details.Text = ex.Message; }
        };
        assemble.Click += async (_, _) =>
        {
            if (catalogues.SelectedItem is not WardrobeCatalogChoice source || kits.SelectedItem is not WardrobeKitChoice kit) return;
            assemble.IsEnabled = browse.IsEnabled = catalogues.IsEnabled = kits.IsEnabled = search.IsEnabled = false;
            try
            {
                await AssembleWardrobeKitAsync(source.Catalog, kit.Id, lookup, text => details.Text = text, cancellation);
                if (_modelAssemblyActive) window.Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { details.Text = ex.Message; }
            finally { if (!cancellation.IsCancellationRequested) { browse.IsEnabled = catalogues.IsEnabled = search.IsEnabled = true; kits.IsEnabled = kits.Items.Count > 0; assemble.IsEnabled = kits.SelectedItem != null; } }
        };
        window.Closed += (_, _) => { lifetime.Cancel(); if (ReferenceEquals(_wardrobeWindow, window)) _wardrobeWindow = null; };
        window.Loaded += async (_, _) =>
        {
            browse.IsEnabled = catalogues.IsEnabled = false;
            try
            {
                int examined = 0;
                foreach (var document in _documents.Values.ToArray())
                    foreach (var entry in document.Entries.Where(e => e.CanExtract && (e.Extension is ".psin" or ".pso" or ".0drp" || e.Compression == RageResourceMetadata.EntryKind || e.Path.Contains("metadata_PSIN", StringComparison.OrdinalIgnoreCase))).Take(2048))
                    {
                        cancellation.ThrowIfCancellationRequested(); examined++;
                        try
                        {
                            var parsed = await Task.Run(() =>
                            {
                                byte[] prefix = document.ReadPreview(entry, 65536, cancellation);
                                if (prefix.AsSpan().IndexOf(new byte[] { 0xf5, 0x4c, 0xcb, 0x8d }) < 0) return null;
                                if (entry.Size is <= 0 or > RageWardrobeCatalog.MaxBytes) return null;
                                return RageWardrobeCatalog.Decode(document.ReadPreview(entry, (int)entry.Size, cancellation));
                            }, cancellation);
                            if (parsed != null) choices.Add(new(Path.GetFileName(document.SourcePath) + " · " + entry.Offset.ToString("X"), parsed));
                        }
                        catch (Exception ex) when (IsTextureReadFailure(ex)) { }
                        if (examined % 32 == 0) details.Text = $"Checked {examined} metadata resources; found {choices.Count} wardrobe catalogues.";
                    }
                cancellation.ThrowIfCancellationRequested(); catalogues.ItemsSource = choices.ToArray();
                if (choices.Count > 0) catalogues.SelectedIndex = 0;
                else details.Text = "No complete wardrobe catalogue was found in the loaded metadata. Scan the relevant RPF metadata, or open an extracted PSIN/0DRP catalogue file.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { details.Text = ex.Message; }
            finally { if (!cancellation.IsCancellationRequested) browse.IsEnabled = catalogues.IsEnabled = true; }
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog(); lifetime.Dispose();
    }

    private static Dictionary<ulong, List<WorkspaceAsset>> BuildWardrobeLookup(IEnumerable<WorkspaceAsset> assets)
    {
        var result = new Dictionary<ulong, List<WorkspaceAsset>>();
        foreach (var asset in assets)
        {
            if (WardrobeIndex.DrawableName(asset.Entry) is not { } candidate) continue;
            ulong key = RageAssetKeys.DrawableKey(candidate);
            if (!result.TryGetValue(key, out var matches)) result[key] = matches = [];
            matches.Add(asset);
        }
        return result;
    }

    private async Task AssembleWardrobeKitAsync(RageWardrobeCatalog catalog, uint kitId,
        Dictionary<ulong, List<WorkspaceAsset>> lookup, Action<string> progress, CancellationToken token)
    {
        var resolution = catalog.Resolve(kitId);
        if (resolution.Warnings.Count > 0) throw new InvalidDataException("The authored kit has unresolved references: " + string.Join(" ", resolution.Warnings.Take(4)));
        var groups = resolution.Selections.Where(s => s.DrawableKey != 0).GroupBy(s => s.DrawableKey).ToArray();
        if (groups.Length is < 1 or > 96) throw new InvalidDataException("Choose a kit with 1 to 96 model components.");
        var staticOptions = new WardrobeModelOption?[groups.Length]; var stateAlbedos = new ulong[groups.Length];
        var stateTints = new (uint Palette, byte Index)?[groups.Length];
        for (int component = 0; component < groups.Length; component++)
        {
            var group = groups[component]; token.ThrowIfCancellationRequested();
            var states = group.Select(s => (s.AlbedoKey, s.NormalKey, s.ExtraKey, s.PaletteHash, s.Tint0, s.Tint1, s.Tint2)).Distinct().ToArray();
            if (states.Length > 1) throw new InvalidDataException($"Kit {kitId:X8} selects conflicting appearance states for drawable {group.Key:X16}.");
            stateAlbedos[component] = states[0].AlbedoKey;
            if (states[0].PaletteHash != 0 && states[0].Tint0 == states[0].Tint1 && states[0].Tint0 == states[0].Tint2)
                stateTints[component] = (states[0].PaletteHash, states[0].Tint0);
            progress($"Validating component {component + 1} / {groups.Length}, native LODs 0 to 3…");
            if (!lookup.TryGetValue(group.Key, out var candidates)) throw new InvalidDataException($"Drawable {group.Key:X16} is not indexed in the loaded model sources.");
            if (candidates.Count > 128) throw new InvalidDataException($"Drawable {group.Key:X16} exceeds the supported physical variant limit.");
            foreach (var candidate in candidates.Take(128))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var models = await Task.Run(() =>
                    {
                        byte[] bytes = ArchiveReader.ReadResourceData(candidate.Document, candidate.Entry, token);
                        int split = candidate.Entry.ResourceSystemBytes ?? checked((int)candidate.Entry.Segments![0].Size);
                        var decoded = new List<WardrobeModelOption>();
                        if (split <= 0 || split >= bytes.Length) return decoded;
                        for (int lod = 0; lod < 4; lod++)
                        {
                            token.ThrowIfCancellationRequested();
                            var model = RageModelDecoder.Decode(bytes.AsSpan(0, split), bytes.AsSpan(split), candidate.Entry.ResourceDrawableOffset, lod);
                            if (model != null && RageAssetKeys.DrawableKey(model.Name) == group.Key)
                                decoded.Add(new(candidate, model, lod, model.Meshes.Sum(m => m.Indices.Count / 3)));
                        }
                        return decoded;
                    }, token);
                    foreach (var option in models)
                    {
                        if (staticOptions[component] is not { } previous || option.Triangles > previous.Triangles) staticOptions[component] = option;
                    }
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { }
            }
            if (staticOptions[component] == null) throw new InvalidDataException($"Drawable {group.Key:X16} has no readable validated model yet. Its RPF download may be incomplete.");
            var retained = staticOptions.OfType<WardrobeModelOption>().ToArray();
            if (retained.Sum(o => (long)o.Model.Meshes.Sum(m => m.Positions.Count)) > 8_000_000 || retained.Sum(o => (long)o.Model.Meshes.Sum(m => m.Indices.Count)) > 24_000_000)
                throw new InvalidDataException("The wardrobe variants exceed the supported validation memory budget.");
        }
        var selected = staticOptions.OfType<WardrobeModelOption>().ToArray();
        var selection = new List<(FileRow Row, ArchiveDocument Document)>(); var meshes = new List<RageMesh>();
        var headNames = new HashSet<string>(StringComparer.Ordinal); var albedos = new Dictionary<(string, string, long, int?), ulong>();
        var tints = new Dictionary<(string, string, long, int?), (uint Palette, byte Index)>(); int vertices = 0, indices = 0;
        for (int component = 0; component < selected.Length; component++)
        {
            var option = selected[component]; var best = option.Asset; var model = option.Model;
            int addedVertices = model.Meshes.Sum(m => m.Positions.Count), addedIndices = model.Meshes.Sum(m => m.Indices.Count);
            if (addedVertices > 2_000_000 - vertices || addedIndices > 6_000_000 - indices || model.Meshes.Count > 1024 - meshes.Count) throw new InvalidDataException("The wardrobe exceeds the model preview budget.");
            vertices += addedVertices; indices += addedIndices; meshes.AddRange(model.Meshes);
            if ((model.Name.StartsWith("head_", StringComparison.OrdinalIgnoreCase) || model.Name.Contains("_head_", StringComparison.OrdinalIgnoreCase)) && !model.Name.Contains("_eyes_", StringComparison.OrdinalIgnoreCase)) headNames.Add(model.Name);
            selection.Add((new FileRow(best.Entry) { Asset = best, ModelLod = option.Lod }, best.Document));
            if (stateAlbedos[component] != 0) albedos[(best.SourcePath, best.Entry.Path, best.Entry.Offset, best.Entry.ResourceDrawableOffset)] = stateAlbedos[component];
            if (stateTints[component] is { } tint) tints[(best.SourcePath, best.Entry.Path, best.Entry.Offset, best.Entry.ResourceDrawableOffset)] = tint;
        }
        progress("Reading native appearance colors…");
        await LoadWardrobeAppearanceAssetsAsync(token);
        token.ThrowIfCancellationRequested(); _wardrobeAlbedos.Clear(); foreach (var entry in albedos) _wardrobeAlbedos.Add(entry.Key, entry.Value);
        _wardrobeTints.Clear(); foreach (var entry in tints) _wardrobeTints.Add(entry.Key, entry.Value);
        try
        {
            using var registration = token.Register(() => _workCancellation?.Cancel());
            await PreviewModelAssemblySourcesAsync(selection.ToArray());
        }
        finally { _wardrobeAlbedos.Clear(); _wardrobeTints.Clear(); }
        token.ThrowIfCancellationRequested();
        if (_modelAssemblyActive)
        {
            if (_decodedModel?.Meshes.Count != meshes.Count || ModelPreview.PresentedVertices != vertices || ModelPreview.PresentedTriangles != indices / 3)
                throw new InvalidDataException("The complete authored kit could not be assembled. Some source components may have become unavailable.");
            PreviewNameText.Text = $"Authored wardrobe kit {kitId:X8}";
            PreviewNameText.ToolTip = $"Native authored kit {kitId:X8}. Every selected drawable ID is matched against its decoded resource name.";
            ModelAssemblyBanner.Text = $"Authored kit {kitId:X8} · {groups.Length} exact model IDs · static native LODs";
            if (headNames.Count == 1) ModelAssemblyBanner.Text += " · " + headNames.Single();
            const string presentation = "Authored wardrobe components shown in their stored pose. Native shader effects, body morphs and cloth simulation are not reproduced.";
            if (_decodedModel != null) _decodedModel = _decodedModel with { Name = $"wardrobe_{kitId:X8}", PresentationNote = presentation };
            PreviewFootnoteText.Text = $"{ModelPreview.PresentedTexturedMeshes:N0} textured mesh parts · {ModelPreview.PresentedSlodColoredMeshes:N0} with SLOD color approximation · {_modelMissingTextureMaterials:N0} unresolved materials · static preview · native shader effects are not applied";
            TextPreview.Text = $"Authored wardrobe kit {kitId:X8}\nThe selected components are resolved by exact 64-bit drawable identifiers and validated native model names.\nStored model coordinates are retained.\n\n" + TextPreview.Text;
            StatusText.Text = $"Wardrobe kit {kitId:X8} assembled as a static preview.";
            ModelPreview.FitToView();
        }
    }
}
