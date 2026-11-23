# RPF9 archive index research

The protected headers of three local GTA 6 RPF9 samples were decoded in a separate research tool. Two implementations agreed byte for byte on the resulting headers. One implementation was checked against 64 block transform inputs from an isolated emulation run.

| Header bytes | Entry slots |
| ---: | ---: |
| 864 | 2 |
| 38,272 | 418 |
| 17,744 | 172 |

The header uses three transform stages in these samples. The first stage chains across two metadata spans. It reveals the descriptor used for the remaining two spans, each of which starts with fresh chaining state.

The decoded index declares six record tables. Each entry has indices into those tables, and different entries can share a table record. Each index either points to a record inside its declared table or uses a sentinel for a missing record. Every index and record range fit its declared bounds across the three sample headers.

## Reading members from the index

The local research reader now uses the index to find member offsets and sizes. It validated every chunked member in the three root samples against its declared decoded length. Compressed chunks also passed zlib checksum validation.

| Header bytes | Validated chunked members | Members requiring a counter transform | Nested RPF headers decoded |
| ---: | ---: | ---: | ---: |
| 864 | 2 | 2 | 0 |
| 38,272 | 418 | 149 | 0 |
| 17,744 | 113 | 11 | 59 |

The 59 nested headers contain 80,298 entry slots. The local reader checked all 485 nested data members and all 79,813 resource payloads. The full resource pass produced 9,722,110,144 decoded bytes. All 143,017 zlib chunks passed checksum validation, all 1,157 raw chunks matched their declared sizes, and every payload matched its declared system and graphics lengths.

The reader also decoded the indexes of 22 standalone RPF9 files, including the animation, audio, level, movie, shader, and texture archives. The 6,534 top-level slots in this survey include 5,150 directly stored members, 1,383 chunked data members, and one chunked resource. All 1,384 chunked payloads passed their length and codec checks.

Of the directly stored members, 797 had Bink movie headers and frame tables that agreed with their indexed lengths. Frame table checks covered offset order, bounds, and the final frame boundary. They did not decode the video frames.

The survey decoded headers for all 4,351 directly stored child archives found in the standalone indexes. Those children contain 1,248,505 entry slots.

## Full read of the local archive set

The local decoder read all 1,255,039 indexed entries across the 22 standalone archives and their 4,351 children. Every member produced its declared byte count. The pass verified 2,243,921 zlib chunk checksums, checked the lengths of 22,840 stored chunks, and found no further RPF9 child headers in the decoded member prefixes.

| Storage | Entries read |
| --- | ---: |
| Directly stored | 76,826 |
| Chunked data | 24,492 |
| Chunked resources | 1,153,560 |
| Raw DEFLATE | 161 |

The pass processed 564,783,265,279 decoded bytes, counting each child archive as a member and then reading the entries inside it. Directly stored data was checked against index bounds and length. Asset interpretation, including geometry, materials, and audio formats, requires separate checks.

## Encrypted and compressed storage

Across the 22 standalone and 4,351 directly stored child indexes, 1,398 members carried an explicit payload encryption descriptor. Every observed descriptor used type 17 with flag 4. Full reads validated every one of those members.

| Verified storage | Members |
| --- | ---: |
| Encrypted chunked data | 648 |
| Encrypted chunked resources | 746 |
| Encrypted raw DEFLATE | 4 |
| Clear raw DEFLATE | 157 |
| Layout 4 chunked resources | 14,432 |
| Layout 4 chunked data | 56 |

Encrypted resources have a 16-byte clear prefix. The counter starts at the first byte after that prefix. Raw DEFLATE streams end at their declared decoded size and have up to 15 bytes of zero alignment padding. Layout 4 encodes each chunk's decoded length, padded stored length, padding and codec in an eight-byte record.

## Resource checks

In 46 system-only entries from three child archives, the decoded bytes at offset `0x10` spell `0DRP`. The next little-endian word contains version 122. The root block number at `0x20` is one-based. The system pointer at `0x38` and the count at `0x4C` locate a directory of 16-byte records. Each record holds a type hash, a byte length and a system pointer. Subtracting `0x50000000` from those pointers locates the blocks within the decoded system section.

The structure table at the pointer in `0x28` describes root fields by hash, type and byte offset. In one level resource, the root schema described an array at offset `0x18` with 19 items starting at block 2. The preview reports field hashes directly. An array's purpose and the names of its fields still require a matching schema dictionary.

Eight sampled fragments contained 15 populated drawables. The count at fragment offset `0x30` includes the primary record. Additional records are spaced `0x110` bytes apart from the primary. Two samples had an empty primary, and one had two populated children alongside its primary. Explicit selection and enumeration produced identical OBJ geometry for each populated record. The role of the child geometry in the game remains unknown.

## Header signatures

A separate pass verified the signatures of all 22 standalone headers and all 4,351 observed child headers with the RSA-2048 public key embedded in the analyzed build. The format uses PKCS #1 v1.5 with double SHA-256 over selected header ranges after the first transform. Controlled changes to covered bytes failed verification. These checks cover the signed header ranges. The key came from the local executable and its provenance was not independently established.

## Looking up names

The index stores 32-bit and 64-bit hashes of relative paths. Lookup treats recognized extensions as a separate type and normalizes ASCII case and slash direction. Tests against isolated native instructions covered 339 hash comparisons.

Searching executable strings and decoded metadata produced candidates for 5,608 of the 6,534 standalone slots. Of those, 1,378 match both hashes and the entry type, including all 797 indexed movies. Another 4,230 child archive candidates match their available 64-bit hash and entry type. Some script matches identify a name stem and extension family. Original casing and unmatched paths remain unresolved.

Testing 458,892 unambiguous typed path candidates from the local metadata against the 1,248,505 nested slots produced no additional path matches. Drawable and fragment resources can carry their own model labels, and drawable dictionaries can carry several child labels. Across the 59 child archives in the three-sample study, the desktop reader found embedded names for all 27,288 recognized model entries, with 27,484 labels across those entries. These labels are searchable. Original archive paths and archive rebuilding remain open areas of work. See the [desktop decoder guide](rpf9-index-provider.md).
