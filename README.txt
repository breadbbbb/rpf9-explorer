rpf9-explorer 1.5 - Windows desktop app

Open README.md for the illustrated guide and current format limitations.

This is an unofficial project and is not affiliated with Rockstar Games or
Take-Two Interactive. The source contains no actual GTA 6 game code. Game source
code, decompiled game code, executables and scripts are not included in the
source package. Screenshots show files loaded locally.

With a compatible local decoder, this application browses the RPF9 member table,
opens nested archives and previews or extracts supported members. Unmatched
names are identified by slot and hash. The built-in recovery scanner is also
available. Archive rebuilding remains unavailable.

USING THE APP

In recovery mode, model discovery scans the whole RPF9 source on first open and refreshes older
model indexes automatically. Character parts inside drawable dictionaries are
listed and searchable by native component name. Discovery shows progress and can
be canceled; completed model indexes are reused on later opens.

Ctrl/Shift-select two or more model rows and choose Preview together to view the
selected components in their original coordinates. Export OBJ saves the combined
static geometry. Assembly details lists included and omitted components. Body
morphs and strand hair remain unsupported. File > Assembled wardrobe resolves
authored outfit kits from supported PSIN/0DRP catalogues as static previews.
Add selected parts collects components across different searches, up to 64 parts.
Preview queued parts shows the collected components; Clear parts empties the queue.
Extract selected model entries writes standard OBJ geometry for the selected
dictionary child. Original archives remain unchanged.

Run rpf9-explorer.exe with its Tools folder beside it. Open or drop multiple RPF,
ZIP and TTPF files together. Open folder loads these formats recursively, up to
512 sources; opening more files adds them to the current workspace. Clear loaded
sources starts a new workspace. Select a file to preview it; panes are resizable.

IMAGE SEARCH AND WORKSPACES

Choose Images to search individual textures across all loaded sources, including
images inside native texture dictionaries. Thumbnails shows a paged image gallery
(96 images per page). Ctrl/Shift selects multiple images for extraction. Metadata
indexing reads texture descriptors without decompressing every graphics payload;
thumbnails decode on demand. Unavailable codecs remain labelled unavailable.

Search matches all words or quoted phrases, with -word exclusions and wildcards.
Fields: name:, source: (or archive:), type:, format:, key: (or hash:), width:,
height:, mips: and size:. Numeric fields accept >, >=, <, <= and =; sizes accept
KiB/MiB/GiB. Examples:
  type:image name:glass -normal
  format:bc7 width:>=1024 height:>=512 size:<2MiB
  source:ttdb_maps key:995C5B7E3D7797BD
Search all sources ignores the current folder while a query is entered. Uncheck
it to search only the current location. Selecting a tree folder clears the query.
Streaming pool filenames remain generated native-key names where original names
are unknown; no descriptive names or associations are invented.

Expand Sources and textures to open a texture dictionary like a folder, or
double-click its Texture folder row. Its images become searchable, selectable
children with actual DDS sizes. Expand a TTPF pool to browse its texture records;
large tree lists load in batches. Up returns to the containing folder or source.

Models resolve supported diffuse images by native asset key from embedded model
textures and loaded texture sources, including pools outside the game's usual
folder layout. Sibling game pools are a fallback. A small set of validated
resident defaults also resolves by key, name hash and pixel fingerprint.
Staged texture edits in this workspace override
matching source images. Preview images are bounded/cached; unsupported codecs or
unresolved material references retain neutral materials.

Save workspace (Ctrl+S) creates .rpfworkspace plus a sibling .rpfworkspace.assets
folder. Keep them together. It stores all source paths, search, folder, selection
and independently bound staged edits. Imports/removals persist automatically;
Ctrl+S also saves the current view. Open workspace restores them. Sources must
remain at their saved paths with matching fingerprints. A workspace is not a
backup of the original archives. Legacy .rpfproject files still open and can be
copied into a new multi-source workspace using Save workspace.

REPLACEMENT PROJECTS

Select an asset and choose Stage replacement. Models accept OBJ, textures accept
DDS/PNG/JPEG, audio streams accept WAV, and other files accept an opaque binary
replacement. Imports are copied into per-source .rpfproject files managed by the
saved workspace, with
source binding, SHA-256, per-member/subresource targets and atomic manifest saves.
Use Original / Staged replacement to compare, or Remove replacement to revert
the project entry. Open workspace restores all saved source-specific changes.

These are staged replacement assets. They have NOT been applied to the original
RPF9 archive. A validated original-directory and archive writer, including size
and integrity updates, is still missing; a staged preview is not an in-game mod.
Arbitrary model geometry also needs native culling/meshlet metadata rebuilding.

For an exactly compatible DDS replacing a recovered texture-dictionary entry,
Build native texture writes a decoded .r9texdata bundle with the selected texture
re-tiled. Dimensions, pixel format, mip count and array/volume shape must match.
System data, padding and other textures are preserved. It does not rewrite RPF9.

For a supported complete-mip DDS replacement in an RDS5 .ttpf pool, Export pool
copy (experimental) writes a new native pool file. Only affected chunks are
re-tiled/recompressed; virtual ranges, metadata and other stored chunks remain
unchanged. The copy is reopened and its selected DDS is verified. Unknown
metadata/integrity semantics are retained; game compatibility has NOT been
verified. Original files are never overwritten. Progress/cancellation are
available and incomplete temporary outputs are removed.

For RPF9, Scan entire archive reads the whole source and caches supported model,
texture and audio payloads. Large archives can take several minutes. Progress
and cancellation are available. Completed indexes are stored locally under
%LOCALAPPDATA%\RpfPreview\Indexes and bound to the source path, size, timestamp
and header hash. The source is never modified. New or changed sources invalidate
their old cache. Opening without a cache uses bounded format-specific searches.
The scan still cannot recover unsupported formats or the original directory.

PREVIEWS AND STANDARD EXPORTS

Models: supported native drawables and fragment child drawables, validated
positions, vertex layouts, meshlet triangles, pointers and bounds. Drag to orbit,
right-drag to pan, wheel to zoom, Reset view or R to reset. Export OBJ saves the
validated geometry and supported UVs. Exact native asset keys resolve supported
diffusetex color textures in base-game and pack-local data\ttdb pools. Float2 and
shader-proven SNORM16 UV coordinates are supported; unavailable texture codecs or
unresolved bindings retain neutral materials. Full game shader effects, bone
physics and full game shaders are not reproduced. Models use their stored pose.
Character animation playback is not included.

Core integrations can select a model's level of detail with
RageModelDecoder.Decode(system, graphics, drawableOffset, lod), where lod is
0 to 3. The returned model records the selection in Lod. Existing calls and
individual desktop model previews use LOD 0. Wardrobe assembly can select a
readable native detail variant. An unavailable LOD returns null.

Textures: observed AMD-tiled native dictionaries, BC1–BC7 and supported raw/
floating-point formats. Includes HDR, arrays, cube faces and 3D-volume slices.
Choose a texture, then a slice/face where applicable. Export DDS preserves source
mip levels, array/volume content and HDR values. The screen preview is 8-bit;
floating-point values are clipped to 0–1 and large images use an available smaller
mip. Format/resolution notes are displayed.

Streaming textures: open or drop an RDS5 .ttpf pool to browse its native ETPF634
catalog. Validated 2D, compact, array, cubemap and 3D-volume allocations with
stored, zlib or BCPack (BC1/BC3/BC4/BC5) chunks preview and export DDS. Some
textures export only their supported mips; the app and DDS header identify that
accurately. Catalog IDs are opaque and original filename semantics remain unknown.
Unsupported BCPack codecs (BC6H/BC7), unvalidated storage profiles and unsupported
formats are listed with their limitations. Original filenames remain unknown;
supported external diffuse material references use the separately validated native
64-bit asset keys.

Audio: validated AWC banks, independent Opus, streamed Opus music channels, PCM
and nonstreamed XMA2 via a local FFmpeg backend. Choose a sound/channel and press
Play; pause, seek, stop, loop and volume are available. Save WAV exports the decoded
preview. Previews are limited to 120 seconds. Opus packet padding/encoder trim
placement is unresolved, so previews preserve complete decoded packets. The UI
clock uses the actual PCM duration. Codec-2 prepared filter data is not played as
if it were an ordinary sound. Extract selected preserves the referenced AWC span.

Movies: recovered Bink videos stream directly from their archive range. Play/pause,
seek, stop, step, loop, volume and full screen are available. Double-click for full
screen; Esc exits, Space plays/pauses, Left/Right seek five seconds. The first audio
track plays when present. Preview width is capped at 960 pixels. HDR Bink is not
supported by the CPU preview decoder. No full movie extraction is needed to watch.

Metadata: checksum-validated XML and complete PSIN/LSCH metadata containers. PSIN
has a section/embedded-text/hex preview; typed schema and script bytecode semantics
are not decompiled. The internal CHKS algorithm remains unknown; enclosing zlib
checksums, complete section boundaries and declared total lengths are checked.

Shaders: exact named raw-DEFLATE wrappers and DXBC containers, with independent
DEFLATE completion/size and chunk-bound checks. The Structure tab describes the
compiled Xbox container; shader code is never executed. Shader extraction is
payload recovery and does not reveal the original RPF9 member directory.

PSIN metadata: Scan entire archive finds containers anywhere in an RPF9 file,
including containers with stored (uncompressed) 64 KiB blocks, and lists one
only when its CHKS checksum matches. Both section lists (LSCH... and PMAP...)
are accepted. Field meanings are not decoded.

Animations: clip dictionaries are listed by the name stored inside them. The
Details tab shows the animation count and clip names only. Playback is not
included. Motion-matcher sets use a different record layout and are not listed.
Run Scan entire archive to find dictionaries beyond the initial search range.

Assembled wardrobe: load the catalogue and model sources, then choose File >
Assembled wardrobe. Pick a native kit and assemble it. Native drawable IDs,
appearance keys and model names are checked. The most detailed readable native
variants are shown together in their stored coordinates. A kit's ID does not
establish character identity or guarantee a complete body. Load texture pools
for supported color textures. Body morphs, cloth simulation and strand hair
remain unsupported.

Sources may be read while downloading. Only complete validated resource ranges
are usable. Indexes saved while a writer is active stay partial. Refresh after
the download finishes; an allocated file size does not prove data is complete.

SUPPORTED ARCHIVES AND EXTRACTION

Open little-endian RPF7 and ordinary ZIP: browse, filter, preview and extract
supported members. Encrypted/protected indexes and unsupported resource layouts
are identified. RPF9: format-specific payload recovery, not original TOC decoding.

Use Export OBJ / Export DDS / Save WAV for standard model, image and audio files.
Extract selected / Extract results saves original assets into a new folder,
grouped into distinct per-source subfolders. Extract results uses the current
filtered results. Texture children export standard DDS; dictionary parent rows
export their recovered payload. Staged edits are saved in the workspace and are
not substituted into original-asset extraction.
The .r9modeldata and .r9texdata recovery exports concatenate validated decoded
system and graphics pages; they are application-specific research bundles, not
invented native RSC9 files. Their section split and exact compressed ranges remain
in the source index. Original archive paths are unknown.

Existing extraction outputs are never overwritten. Cancellation removes the
current incomplete file; completed files remain. Unsafe paths, reserved Windows
names, archive links and destination reparse points are rejected. Numeric Size
and Offset sorting use bytes/offsets rather than formatted strings.

SOURCE AND VALIDATION

App: .NET 10 WPF; Core/Tests: .NET 8. The published x64 app is self-contained.
Build:
  dotnet build App\RpfPreview.App.csproj -c Release
  dotnet run --project Tests\RpfPreview.Tests.csproj -c Release
  powershell -File .\build.ps1

Tests include generated malformed/truncated fixtures and real resource/hash
comparisons, independent texture detiling and packed-vertex regressions. Native
PCM and actual WPF preview/playback checks are retained under Verification.
No downloaded game program, DLL, script or shader is executed. Native preview
tools are built locally with trusted installed SDKs. FFmpeg is a separate codec
process built with networking disabled and only the file and pipe protocols.
Archives, decoder files, and extraction destinations must be on local drives.

Third-party notices and licenses are in Licenses. BCnEncoder.Net 2.3.0 and
CommunityToolkit.HighPerformance 8.4.0 are pinned. AMD address-layout attribution,
libopus and the separate FFmpeg build and license notices are included.
No downloaded game assets or game decryption keys are bundled with this app.

EXTERNAL BCPACK HISTORY

Core integrations can load local BC1/BC3/BC4/BC5 history tables with
BcpackHistory.Load(path) and pass them to decoding or export overloads.
The desktop viewer uses the built-in tables. History files are not bundled.
BC6H/BC7 history tables remain unsupported.

MICROSOFT SILICON-RANS NOTICE

BCPack probability models use tables from Microsoft Silicon-rANS:
https://github.com/microsoft/silicon-rans/tree/2f6827d8a4e6a78029c372f20d8b61f7cd408fc5

MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE

APPEARANCE AND VEHICLE PREVIEWS

Use Dark mode at the top right. The preference is remembered.
Supported vehicle materials use approximate paint, rubber, glass and lights.
Load the body and wheel resources, select a vehicle fragment with wheel mounts,
and choose Assemble vehicle. Export OBJ saves assembled geometry.
Optional vehicles.json configuration supplies paint, scales and part names.
Generic fallback wheels are preview choices; complete native shading is not
reproduced. See README.md for details.

CHARACTER TEXTURES

Finish downloading the relevant data/ttdb/peds texture pools, then assemble
the outfit again. Use Scan entire archive to index its appearance metadata.
Actual images are preferred. Supported materials without an available image
can use their native far-distance SLOD color as a uniform approximation.
The preview counts these separately and reports unresolved materials.
Supported uniform tint selections can also use their native palette SLOD color.
Composite appearances, ambiguous materials and full surface shading remain unsupported.
