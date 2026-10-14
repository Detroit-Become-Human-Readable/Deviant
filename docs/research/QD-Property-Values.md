# QD property values

`QdPropertyValueReader` reads one exact `NOCHK` value payload and returns a flat node list plus ordered child edges. It preserves the stored property-name hash as a `uint`; the reader does not map hashes to names or attach gameplay, story or UI meanings.

## Serialized profiles

Every writer-produced value starts with a tag byte and a little-endian 32-bit name hash at `+1`. The tag keeps its original bits; the low nibble selects the value type, and the high bit marks the writer-produced profile.

| Type | Stored value |
| --- | --- |
| 0 | One raw boolean byte at `+5`; the original byte is retained. |
| 1 | Signed integer bytes at `+5`; mode `0x00`, `0x10`, `0x20`, `0x30` selects 1, 2, 4 or 8 bytes. |
| 2 | Number bytes at `+5`; mode `0x00` is signed 8-bit, `0x10` is a signed 16-bit integer divided by 100, `0x20` is float32 and `0x30` is float64. |
| 3 | Little-endian byte length at `+5`, followed by the exact UTF-8 bytes at `+9`. Invalid UTF-8 remains available as raw bytes and receives replacement text for display. |
| 4, 5, 6 | Little-endian element count at `+5`, followed by opaque packed bytes at `+9`. Writer evidence establishes widths for low-six-bit tags `0x04`, `0x05`, `0x15`, `0x25`, `0x26`, `0x35` and `0x36`: 1, 1, 2, 4, 4, 8 and 8 bytes respectively. Element meaning remains unassigned. |
| 7 | The writer’s default five-byte header. The reader preserves it as an unknown header-only value. |
| 8, 9 | Object and array containers. A little-endian 16-bit child count at `+5` is followed by relative child offsets at `+7`. Offsets are three-byte big-endian unless tag flag `0x40` selects four-byte little-endian entries. |

Numeric nodes keep their encoded mode and raw bits. The display string uses invariant formatting, while a separate finite flag identifies NaN and infinity. Container edges retain their child ordinal, encoded relative offset and target identity.

## Graph and extent fields

Each node identity combines the archive resource identity with its payload offset. Equal property hashes remain separate nodes. Repeated child offsets remain separate edges that target a node marked as aliased. Cycles, overlapping node-owned byte ranges, unassigned payload gaps, invalid offsets, unsupported modes and configured limit breaches return diagnostics with the source payload hash and affected extent.

`OwnExtent` describes bytes stored directly by one node. `SubtreeRange` gives a bounding range that may enclose bytes owned by sibling nodes. `SubtreeExtent` is provided only when the reader establishes that the reachable node bytes form one contiguous span. `SubtreeExtentIsContiguous` is true only for that established case; false means the reader makes no contiguous-span claim. `ParentNodeId` and `ChildOrdinal` record first discovery. The ordered edge list carries every graph relationship.

Raw payload fields are read-only slices of one owned snapshot and are omitted from JSON serialization. Extents and decoded display values remain available in JSON.

## Corpus coverage

The full reader pass used the selected PC build identified by index content ID `e1a97a33ae8c9e651266dc2370aa5e18b64db697bc83b77f16c84031b62c36a4`. It processed all **855** type-63 resources and **21,551** metadata payloads, matched every resource and metadata digest to the preserved S2 manifests, and consumed every supported payload exactly.

Observed values were 16 booleans, 151,917 signed integers, 173,229 strings, 72,202 objects and 43,177 arrays. Integer mode counts were 21,551 for `0x00`, 161 for `0x20` and 130,205 for `0x30`; mode `0x10` did not occur. The corpus contained no type-2 numbers, packed types 4–6, type 7 values or four-byte container tables. Those layouts remain available from the bounded writer profile; the packed types retain opaque bytes.

The reader is for inspection and export. It does not serialize edited values or resolve names from hashes.
