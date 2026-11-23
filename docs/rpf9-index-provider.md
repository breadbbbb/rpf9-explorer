# Local RPF9 index decoder

Use **File > Configure local RPF9 decoder** to select a configuration for a compatible local decoder you trust. Reopen the archive or folder. The app shows the decoded index and the header signature result in its inspection details.

Double-click a nested archive row to browse it. Use **Up** at the child archive's root to return to its parent. Saving a workspace also saves the active child location. Name candidates are labelled in the row tooltip. Search for a path or use `hash:` with a 32-bit or 64-bit name hash.

Search by an embedded model name with the search box or the `name:` filter. For indexed models without a matched path, the file list shows the embedded name when available. For a drawable dictionary with several names, the row shows the first name and the number of additional names. All child names are searchable. Hover over the row to see its archive path, hashes, and model names. Extraction uses the indexed path.

Supported entries use the app's existing preview and extraction controls. Movie and AWC entries open the existing players when the required tools are installed. Recognized resource layouts open in the model or texture viewer. Other readable resources can be inspected as bytes and extracted as decoded sections. Staged replacements remain in the workspace.

Keep archives, workspace files, decoder inputs, and extraction destinations on local drives. Network paths, mapped network drives, filesystem links, and offline placeholders are rejected.

## Configuration

The desktop app runs the Python interpreter and bridge script selected in the configuration. Replace each placeholder below with an absolute local path, using forward slashes or doubled backslashes in the JSON strings. Only configure a script you trust. The bridge script, its dependencies, the game executable, and any optional name dictionary must already be on your machine.

```json
{
  "version": 1,
  "python": "<Python interpreter path>",
  "script": "<bridge script path>",
  "executable": "<game executable path>",
  "names": "<name dictionary path>"
}
```

The optional `names` field can be omitted. The configured script reads the executable as data and uses its own compatible format decoder. The app does not launch the game. The public source and app package contain the integration and recovery scanners.

Choose **File > Use recovery scans for new opens** to turn off the local decoder. **Scan entire archive** switches the selected source to the built-in recovery view.

## Bridge contract

The app invokes the script with `--executable PATH --archive PATH`, followed by an optional `--inside SLOT` for each child in the current path. It sets `RPF9_EXPLORER_EXTENDED_STORAGE=1` to advertise support for layout 4 chunks, encrypted resource chunks, and the `deflate` storage mode. It then sends one of two commands.

| Command | stdout |
| --- | --- |
| `index [--names PATH]` | Version 1 catalog JSON in UTF-8 |
| `read --slot SLOT --length BYTES --header SHA256` | The requested prefix of one decoded transformed member as binary bytes |

Catalogs include the original file size, the absolute archive base, the archive extent, the signed header size, the SHA-256 digest of the stored header bytes, the signature verification result, and the numbered rows. Chunk rows can provide absolute, bounded zlib or raw segments with their stored and decoded sizes. Rows with a counter transform and rows with mode `deflate` delegate reads to the bridge. For an encrypted resource chunk row, `prefix_bytes` is the clear prefix length and the block offsets refer to stored file positions after it. The app validates the catalog's ranges and compares its stored header digest against the opened file.
