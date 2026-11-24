namespace RpfPreview.Core;

public static class AssetContent
{
    public sealed record Section(string Path, string Label, string Parent, int Depth, int Count);

    public static IEnumerable<Section> Sections(IEnumerable<WorkspaceAsset> assets)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in assets.GroupBy(a => a.ContentGroup, StringComparer.Ordinal))
        {
            int count = group.Count();
            string prefix = "";
            foreach (string label in group.Key.Split(" / "))
            {
                prefix = prefix.Length == 0 ? label : prefix + " / " + label;
                counts[prefix] = counts.GetValueOrDefault(prefix) + count;
            }
        }
        foreach (var (path, count) in counts)
        {
            string[] levels = path.Split(" / ");
            yield return new(path, levels[^1], string.Join(" / ", levels[..^1]), levels.Length - 1, count);
        }
    }

    public static string Group(WorkspaceAsset asset)
    {
        var e = asset.Entry;
        string path = (e.ContentPathHint ?? e.Path).ToLowerInvariant().Replace('\\', '/');
        string[] words = (path + "/" + string.Join('/', e.EmbeddedModelNames) + "/" + asset.Texture?.Name).ToLowerInvariant().Split(['/', '.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        bool Has(params string[] values) => words.Any(w => values.Contains(w, StringComparer.Ordinal));
        string ext = Path.GetExtension(path);
        bool audio = asset.Kind == "audio" || ext is ".awc" or ".wav" or ".ogg";
        bool audioArchive = e.IsNestedArchive && (path.StartsWith("sfx/") || path.StartsWith("audio/") || path.Contains("/audio/")
            || Path.GetFileName(asset.Document.SourcePath).Equals("audio.rpf", StringComparison.OrdinalIgnoreCase));
        if (audio || audioArchive)
        {
            string context = (asset.Document.ArchiveLocation + "/" + path).ToLowerInvariant();
            string[] tokens = context.Split(['/', '\\', '.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
            bool Token(string word) => tokens.Contains(word, StringComparer.Ordinal);
            var mount = context.Split(" / ").SelectMany(part => part.Split('/'))
                .Select(part => part.Split('.')).FirstOrDefault(p => p.Length >= 3 && p[1] == "radiostation");
            if (mount != null && mount[2].Length > 0) return "Audio / Radio stations / " + mount[2].Replace('_', ' ');
            if (Token("policescanner") || Token("dispatch")) return "Audio / Police scanner / Dispatch";
            if (Token("vehicles") && Token("engines")) return "Audio / Vehicles / Engines";
            if (Token("vehicles")) return "Audio / Vehicles";
            if (Token("weapons")) return "Audio / Weapons";
            if (Token("animals")) return "Audio / Animals";
            if (Token("props")) return "Audio / Props";
            if (Token("resident")) return "Audio / Resident sounds";
            if (Token("system")) return "Audio / System";
            if (Token("cloth")) return "Audio / Clothing";
            if (Token("doors")) return "Audio / Doors";
            if (Token("walla")) return "Audio / Crowd voices";
            if (asset.AudioCues.Count > 0 || Token("speech") && Token("scripted")) return "Audio / Dialogue / Scripted";
            if (Token("speech") && Token("vofx")) return "Audio / Dialogue / Voice effects";
            if (Token("speech") && Token("silent")) return "Audio / Dialogue / Silent";
            if (Token("speech") && Token("player")) return "Audio / Dialogue / Player";
            if (Token("speech") && Token("ped")) return "Audio / Dialogue / Pedestrians";
            if (Token("speech") || Token("conversations") || Token("dialogue")) return "Audio / Dialogue";
            if (Token("score") && Token("stems")) return "Audio / Music / Score stems";
            if (Token("score")) return "Audio / Music / Score";
            if (Token("ambient") || Token("ambience")) return "Audio / Ambience";
            if (Token("script")) return "Audio / Scripted sound effects";
            if (Token("streams")) return "Audio / Streamed sounds";
            if (audioArchive && Token("metadata")) return "Audio / Configuration";
        }
        string pool = Path.GetFileNameWithoutExtension(asset.SourcePath).ToLowerInvariant();
        if (asset.PoolTexture != null)
        {
            if (pool.StartsWith("ttdb_maps")) return "World / Map textures";
            if (pool.StartsWith("ttdb_terrain")) return "World / Terrain textures";
            if (pool.StartsWith("ttdb_interiors")) return "World / Interior textures";
            if (pool.StartsWith("ttdb_peds") || pool.StartsWith("peds_loose")) return "Characters / Textures";
            if (pool.StartsWith("ttdb_vehicles")) return "Vehicles / Textures";
        }
        if (words.Any(w => w.StartsWith("radiostationmetadata", StringComparison.Ordinal))) return "Audio / Radio stations / Metadata";
        if (Has("radio", "stations", "radiostation", "radiostations") && (asset.Kind == "audio" || ext is ".awc" or ".rel" or ".xml" || path.Contains("/radio/")))
        {
            var dirs = path.Split('/');
            for (int i = 0; i + 2 < dirs.Length; i++) if (dirs[i] is "stations" or "radiostations" && dirs[i + 1].Length > 0) return "Audio / Radio stations / " + dirs[i + 1];
            return "Audio / Radio stations";
        }
        if (Has("stations", "radiostations") && asset.Kind == "audio") return "Audio / Radio stations";
        if (Has("speechevents", "speechcontextruledictionary", "speechcontext")) return "Audio / Dialogue / Metadata";
        if (Has("scannerreportmetadata", "scannerreportgamestatemappings") || path.Contains("police_scanner", StringComparison.Ordinal)) return "Audio / Police scanner / Metadata";
        if (asset.BankContent.HasFlag(AwcContent.Granular)) return "Audio / Granular sounds";
        if (asset.BankContent.HasFlag(AwcContent.Midi)) return "Audio / MIDI sequences";
        if (asset.BankContent.HasFlag(AwcContent.Multichannel)) return "Audio / Multichannel streams";
        if (asset.BankContent.HasFlag(AwcContent.Animation)) return "Audio / Sounds with animation data";
        if (ext == ".ymap" || Has("ymap", "mapdata", "maptiles", "minimap", "mapzoomdata")) return "World / Maps";
        if (ext == ".ytyp" || Has("archetypes", "ytyp")) return "World / Archetypes";
        if (ext is ".ybn" or ".ynv" or ".ynd" || Has("navmesh", "collision", "pathnodes")) return "World / Collision and navigation";
        if (ext is ".zmap" or ".imap" || Has("heightmap", "watersystemheightmap", "mapdata")) return "World / Maps";
        if (Has("world", "worlds", "terrain", "levels", "interiors") && asset.Kind is not ("audio" or "shader" or "animation")) return "World / Scenes and terrain";
        if (asset.Kind == "shader" || ext is ".cso" or ".dxbc" or ".dxil" or ".spv")
        {
            string stage = Has("ms", "msnc") ? "Mesh" : Has("vs") ? "Vertex" : Has("ps") ? "Pixel" : Has("cs") ? "Compute" : Has("gs") ? "Geometry" : Has("hs") ? "Hull" : Has("ds") ? "Domain" : "Compiled";
            return "Shaders / " + stage;
        }
        if (ext is ".hlsl" or ".glsl" or ".vert" or ".frag" or ".fx") return "Shaders / Source";
        if (asset.Kind == "model" || ext is ".yft" or ".ydr" or ".ydd")
        {
            if (Has("rim", "tyre", "tire", "brakedisc", "caliper", "wheel")) return "Vehicles / Wheels and brakes";
            if (e.HasVehicleMounts || Has("vehicles", "vehicle", "cars", "car", "chassis", "bodyshell")) return "Vehicles / Models and parts";
            if (Has("head", "hair", "feet", "hand", "hands", "torso", "legs", "shirt", "pants", "jacket", "wardrobe", "ped", "peds", "characters", "body", "eyes", "shoes", "upper", "lower", "accs", "jbib", "teef", "berd")) return "Characters / Models and clothing";
            return "Models / Other drawables";
        }
        if (Has("wardrobe", "outfits", "wardrobecatalog")) return "Characters / Wardrobe";
        return asset.Kind switch
        {
            "audio" => "Audio / Sound banks",
            "video" => "Movies",
            "animation" => "Characters / Animation dictionaries",
            "image" => "Textures / Images",
            "dictionary" => "Textures / Dictionaries",
            "metadata" => "Metadata / Structured resources",
            "archive" => "Archives",
            _ when ext is ".awc" or ".wav" or ".ogg" => "Audio / Sound banks",
            _ when ext is ".bk2" or ".bik" => "Movies",
            _ when ext is ".psin" or ".pso" or ".xml" or ".meta" or ".json" => "Metadata / Documents",
            _ when e.IsResource => "Resources / Unclassified",
            _ => "Files / Other"
        };
    }
}
