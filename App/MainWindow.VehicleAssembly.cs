using RpfPreview.Core;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace RpfPreview.App;

public partial class MainWindow
{
    private VehicleConfigCatalog? _vehicleConfigCatalog;
    private string? _vehicleConfigSearchRoot;
    private (ArchiveDocument Document, ArchiveEntry Entry, RageModel Model, IReadOnlyList<VehicleWheelMount> Mounts)? _activeVehicleContext;

    private static Dictionary<int, VehicleMaterialKind> BuildMaterialKindMap(RageModel model, bool isWheelRim)
    {
        var map = new Dictionary<int, VehicleMaterialKind>();
        if (model.Materials == null) return map;
        foreach (var mat in model.Materials)
            map[mat.ShaderIndex] = VehicleAssembly.ClassifyMaterial(mat, isWheelRim);
        return map;
    }

    private VehicleConfigCatalog? GetVehicleConfigCatalog(ArchiveDocument? document)
    {
        string root = document != null ? Path.GetDirectoryName(document.SourcePath) ?? "" : "";
        if (_vehicleConfigCatalog != null && string.Equals(_vehicleConfigSearchRoot, root, StringComparison.OrdinalIgnoreCase))
            return _vehicleConfigCatalog;

        _vehicleConfigSearchRoot = root;
        _vehicleConfigCatalog = null;
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "vehicles.json")
        };
        if (!string.IsNullOrEmpty(root))
        {
            candidates.Add(Path.Combine(root, "vehicles.json"));
            string? parent = Directory.GetParent(root)?.FullName;
            if (!string.IsNullOrEmpty(parent))
                candidates.Add(Path.Combine(parent, "vehicles.json"));
        }

        foreach (string candidate in candidates)
        {
            var loaded = VehicleConfigCatalog.TryLoad(candidate);
            if (loaded != null) return _vehicleConfigCatalog = loaded;
        }
        return _vehicleConfigCatalog;
    }

    private Color? ResolveVehiclePaintColor(ArchiveDocument document, string modelName)
    {
        var catalog = GetVehicleConfigCatalog(document);
        if (catalog != null && catalog.TryGet(modelName, out var preset) && TryParseHexColor(preset.PrimaryColorHex, out var color))
            return color;
        return null;
    }

    private void UpdateVehicleAssemblyControls(ArchiveDocument document, ArchiveEntry entry, RageModel model, IReadOnlyList<VehicleWheelMount> mounts)
    {
        if (mounts.Count > 0)
        {
            _activeVehicleContext = (document, entry, model, mounts);
            if (AssembleVehicleButton != null) AssembleVehicleButton.Visibility = Visibility.Visible;
        }
        else
        {
            _activeVehicleContext = null;
            if (AssembleVehicleButton != null) AssembleVehicleButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void AssembleVehicle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _activeVehicleContext is not { } ctx) return;
        await AssembleCurrentVehicleAsync(ctx.Document, ctx.Entry, ctx.Model, ctx.Mounts);
    }

    private async Task AssembleCurrentVehicleAsync(ArchiveDocument bodyDocument, ArchiveEntry bodyEntry,
        RageModel bodyModel, IReadOnlyList<VehicleWheelMount> mounts)
    {
        if (_assetIndex == null) return;
        var previous = _activePreview;
        _previewCancellation?.Cancel();
        _workCancellation?.Dispose();
        var work = _workCancellation = new CancellationTokenSource();
        SetBusy(true, true);
        try
        {
            if (previous != null)
            {
                try { await previous; }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException) { }
            }
            work.Token.ThrowIfCancellationRequested();
            ClearFragmentChoices();
            _previewCancellation?.Dispose();
            _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
            WorkProgress.IsIndeterminate = true;
            var token = _previewCancellation.Token;
            _activePreview = BuildVehicleAssemblyAsync(bodyDocument, bodyEntry, bodyModel, mounts, token);
            await _activePreview;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Vehicle assembly canceled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not assemble vehicle: " + ex.Message;
        }
        finally
        {
            WorkProgress.IsIndeterminate = false;
            work.Dispose();
            if (ReferenceEquals(_workCancellation, work)) _workCancellation = null;
            SetBusy(false);
            if (_modelAssemblyActive) { StageReplacementButton.IsEnabled = false; ReplacementControls.Visibility = Visibility.Collapsed; }
        }
    }

    private async Task BuildVehicleAssemblyAsync(ArchiveDocument bodyDocument, ArchiveEntry bodyEntry,
        RageModel bodyModel, IReadOnlyList<VehicleWheelMount> mounts, CancellationToken token)
    {
        var catalog = GetVehicleConfigCatalog(bodyDocument);
        VehicleConfigPreset? preset = null;
        catalog?.TryGet(bodyModel.Name, out preset);

        var (frontScale, rearScale) = VehicleAssembly.ResolveWheelScales(preset);
        Color? primaryColor = TryParseHexColor(preset?.PrimaryColorHex, out var pc) ? pc : null;
        Color secondaryColor = Color.FromRgb(184, 190, 200);
        Color caliperColor = Color.FromRgb(180, 28, 32);

        WorkspaceAsset? rimAsset = FindWorkspaceModel(preset?.RimName) ?? FindWorkspaceModel("rim_uni_steel_002") ?? FindWorkspaceModel("rim_muscle_001") ?? FindFirstWorkspaceModelWithPrefix("rim_");
        WorkspaceAsset? tyreAsset = FindWorkspaceModel(preset?.TyreName) ?? FindWorkspaceModel("tyre_car_001") ?? FindWorkspaceModel("tyre_muscle_001") ?? FindFirstWorkspaceModelWithPrefix("tyre_");
        WorkspaceAsset? discAsset = FindWorkspaceModel(preset?.BrakeDiscName);
        WorkspaceAsset? caliperAsset = FindWorkspaceModel(preset?.CaliperName);

        var extraPartAssets = new List<WorkspaceAsset>();
        if (preset != null)
        {
            foreach (string partName in preset.ExtraPartNames)
                if (FindWorkspaceModel(partName) is { } asset) extraPartAssets.Add(asset);
        }

        string baseName = VehicleAssembly.NormalizeVehicleBaseName(bodyModel.Name);
        StatusText.Text = $"Assembling {baseName}…";

        var meshes = new List<RageMesh>();
        var preview = new List<MeshPreviewPart>();
        var notes = new StringBuilder();
        int vertexCount = 0, indexCount = 0;
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);

        void AddPartMeshes(RageModel partModel, IReadOnlyDictionary<int, ImageSource> partTextures,
            Dictionary<int, VehicleMaterialKind> kinds, Color? tint, string prefix, Vector3? pivot = null, float scale = 1f, bool mirrorX = false)
        {
            foreach (var rawMesh in partModel.Meshes)
            {
                token.ThrowIfCancellationRequested();
                if (meshes.Count >= 1024 || rawMesh.Positions.Count > 2_000_000 - vertexCount || rawMesh.Indices.Count > 6_000_000 - indexCount)
                    throw new InvalidDataException("The assembled vehicle exceeds the preview geometry budget.");
                vertexCount += rawMesh.Positions.Count; indexCount += rawMesh.Indices.Count;
                var mesh = pivot is { } p ? VehicleAssembly.TransformMesh(rawMesh, p, scale, mirrorX) : rawMesh;
                string label = prefix + " | " + mesh.Name;
                meshes.Add(mesh with { Name = label });
                foreach (var v in mesh.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                ImageSource? tex = rawMesh.ShaderIndex is int sh && partTextures.TryGetValue(sh, out var img) ? img : null;
                VehicleMaterialKind kind = rawMesh.ShaderIndex is int si && kinds.TryGetValue(si, out var k) ? k : VehicleMaterialKind.Default;
                preview.Add(new(label, mesh.Positions, mesh.Indices)
                {
                    TexCoords = mesh.TexCoords,
                    VertexColors = mesh.VertexColors,
                    Texture = tex,
                    MaterialKind = kind,
                    PaintColor = tint
                });
            }
        }

        async Task<(RageModel Model, IReadOnlyDictionary<int, ImageSource> Textures)?> DecodeAssetAsync(WorkspaceAsset asset)
        {
            var decoded = await Task.Run<(RageModel Model, ReadOnlyMemory<byte> System, ReadOnlyMemory<byte> Graphics)?>(() =>
            {
                token.ThrowIfCancellationRequested();
                byte[] bytes = ArchiveReader.ReadResourceData(asset.Document, asset.Entry, token);
                int split = asset.Entry.ResourceSystemBytes ?? checked((int)asset.Entry.Segments![0].Size);
                if (split <= 0 || split >= bytes.Length) return null;
                var m = RageModelDecoder.Decode(bytes.AsSpan(0, split), bytes.AsSpan(split), asset.Entry.ResourceDrawableOffset);
                return m == null ? null : (m, bytes.AsMemory(0, split), bytes.AsMemory(split));
            }, token);
            if (decoded == null) return null;
            var tex = await ResolveModelTexturesAsync(asset.Document, asset.Entry, decoded.Value.Model, decoded.Value.System, decoded.Value.Graphics, token);
            return (decoded.Value.Model, tex);
        }


        var bodyDecoded = await Task.Run(() =>
        {
            byte[] bytes = ArchiveReader.ReadResourceData(bodyDocument, bodyEntry, token);
            int split = bodyEntry.ResourceSystemBytes ?? checked((int)bodyEntry.Segments![0].Size);
            if (split <= 0 || split >= bytes.Length) throw new InvalidDataException("Invalid vehicle section boundary.");
            return (System: bytes.AsMemory(0, split), Graphics: bytes.AsMemory(split));
        }, token);
        var bodyTextures = await ResolveModelTexturesAsync(bodyDocument, bodyEntry, bodyModel, bodyDecoded.System, bodyDecoded.Graphics, token);
        AddPartMeshes(bodyModel, bodyTextures, BuildMaterialKindMap(bodyModel, isWheelRim: false), primaryColor, bodyModel.Name);
        notes.AppendLine($"Body: {bodyModel.Name} ({mounts.Count} wheel mounts; scale F={frontScale:0.00} R={rearScale:0.00})");


        var rimDecoded = rimAsset != null ? await DecodeAssetAsync(rimAsset) : null;
        var tyreDecoded = tyreAsset != null ? await DecodeAssetAsync(tyreAsset) : null;
        var discDecoded = discAsset != null ? await DecodeAssetAsync(discAsset) : null;
        var caliperDecoded = caliperAsset != null ? await DecodeAssetAsync(caliperAsset) : null;

        if (rimDecoded != null) notes.AppendLine($"Rim: {rimDecoded.Value.Model.Name}");
        if (tyreDecoded != null) notes.AppendLine($"Tyre: {tyreDecoded.Value.Model.Name}");
        if (discDecoded != null) notes.AppendLine($"Brake disc: {discDecoded.Value.Model.Name}");
        if (caliperDecoded != null) notes.AppendLine($"Caliper: {caliperDecoded.Value.Model.Name}");

        var rimKinds = rimDecoded != null ? BuildMaterialKindMap(rimDecoded.Value.Model, isWheelRim: true) : new();
        var tyreKinds = tyreDecoded != null ? BuildMaterialKindMap(tyreDecoded.Value.Model, isWheelRim: false) : new();
        var discKinds = discDecoded != null ? BuildMaterialKindMap(discDecoded.Value.Model, isWheelRim: true) : new();
        var caliperKinds = caliperDecoded != null ? BuildMaterialKindMap(caliperDecoded.Value.Model, isWheelRim: true) : new();

        foreach (var mount in mounts)
        {
            token.ThrowIfCancellationRequested();
            float sc = mount.IsRear ? rearScale : frontScale;
            float bsc = sc * VehicleAssembly.BrakeScaleRatio;
            if (rimDecoded != null)
                AddPartMeshes(rimDecoded.Value.Model, rimDecoded.Value.Textures, rimKinds, secondaryColor, $"{mount.BoneName}_rim", mount.Pivot, sc, mount.IsRight);
            if (tyreDecoded != null)
                AddPartMeshes(tyreDecoded.Value.Model, tyreDecoded.Value.Textures, tyreKinds, null, $"{mount.BoneName}_tyre", mount.Pivot, sc, mount.IsRight);
            if (discDecoded != null)
                AddPartMeshes(discDecoded.Value.Model, discDecoded.Value.Textures, discKinds, secondaryColor, $"{mount.BoneName}_disc", mount.Pivot, bsc, mount.IsRight);
            if (caliperDecoded != null)
                AddPartMeshes(caliperDecoded.Value.Model, caliperDecoded.Value.Textures, caliperKinds, caliperColor, $"{mount.BoneName}_caliper", mount.Pivot, bsc, mount.IsRight);
        }


        foreach (var partAsset in extraPartAssets)
        {
            token.ThrowIfCancellationRequested();
            var partDecoded = await DecodeAssetAsync(partAsset);
            if (partDecoded == null) continue;
            AddPartMeshes(partDecoded.Value.Model, partDecoded.Value.Textures, BuildMaterialKindMap(partDecoded.Value.Model, isWheelRim: false), primaryColor, partDecoded.Value.Model.Name);
            notes.AppendLine($"Mod part: {partDecoded.Value.Model.Name}");
        }

        token.ThrowIfCancellationRequested();
        _decodedModel = new RageModel(baseName + "_assembled", meshes.ToArray(), min, max)
        {
            IsFragment = true,
            HasBoneBindings = bodyModel.HasBoneBindings,
            Materials = bodyModel.Materials,
            PresentationNote = "Assembled vehicle preview (body + mounted wheels/brakes + optional vehicles.json config parts)."
        };
        _modelAssemblyActive = true;
        ModelPreview.ShowMeshes(preview);
        ModelTab.IsEnabled = true; PreviewTabs.SelectedItem = ModelTab;
        PreviewNameText.Text = $"{baseName} · assembled vehicle";
        PreviewMetaText.Text = $"{ModelPreview.PresentedVertices:N0} vertices · {ModelPreview.PresentedTriangles:N0} triangles · {mounts.Count} wheels mounted";
        ModelAssemblyBanner.Text = $"Assembled vehicle · {mounts.Count} wheel mounts" +
            (rimDecoded != null ? $" · {VehicleAssembly.NormalizeVehicleBaseName(rimDecoded.Value.Model.Name)}" : "") +
            (tyreDecoded != null ? $" · {VehicleAssembly.NormalizeVehicleBaseName(tyreDecoded.Value.Model.Name)}" : "") +
            (extraPartAssets.Count > 0 ? $" · {extraPartAssets.Count} mod parts" : "");
        ModelAssemblyBanner.Visibility = Visibility.Visible;
        TextTab.Header = "Assembly details";
        TextPreview.Text = notes.ToString();
        StatusText.Text = $"Assembled {baseName} with {mounts.Count} mounted wheels.";
    }

    private WorkspaceAsset? FindWorkspaceModel(string? targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || _assetIndex == null) return null;
        string baseName = VehicleAssembly.NormalizeVehicleBaseName(targetName);
        WorkspaceAsset? best = null;
        int bestRank = 0;
        foreach (var asset in _assetIndex.Assets)
        {
            if (asset.Kind != "model" || !asset.Available) continue;
            if (!string.Equals(VehicleAssembly.NormalizeVehicleBaseName(WardrobeIndex.DrawableName(asset.Entry) ?? asset.Name), baseName, StringComparison.OrdinalIgnoreCase)) continue;
            string low = asset.Name.ToLowerInvariant();
            int rank = low.Contains("_hi", StringComparison.Ordinal) ? 3 : low.Contains("_mid", StringComparison.Ordinal) ? 2 : 1;
            if (rank > bestRank) { best = asset; bestRank = rank; if (rank == 3) break; }
        }
        return best;
    }

    private WorkspaceAsset? FindFirstWorkspaceModelWithPrefix(string prefix)
    {
        if (_assetIndex == null) return null;
        return _assetIndex.Assets.FirstOrDefault(a => a.Kind == "model" && a.Available
            && (WardrobeIndex.DrawableName(a.Entry) ?? a.Name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.Contains("_hi", StringComparison.OrdinalIgnoreCase))
            ?? _assetIndex.Assets.FirstOrDefault(a => a.Kind == "model" && a.Available
            && (WardrobeIndex.DrawableName(a.Entry) ?? a.Name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        if (byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            color = Color.FromRgb(r, g, b);
            return true;
        }
        return false;
    }
}
