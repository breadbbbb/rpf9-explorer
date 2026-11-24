namespace RpfPreview.Core;

public static class AssetContent
{
    public static string Group(WorkspaceAsset asset)
    {
        var e = asset.Entry;
        string path = e.Path.ToLowerInvariant().Replace('\\', '/');
        string[] words = (path + "/" + string.Join('/', e.EmbeddedModelNames) + "/" + asset.Texture?.Name).ToLowerInvariant().Split(['/', '.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        bool Has(params string[] values) => words.Any(w => values.Contains(w, StringComparer.Ordinal));
        string ext = e.Extension;
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
