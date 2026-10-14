# Physics type layouts

`PhysicsTypeLayoutsReader` connects TBDY layout records to the declarations from `PhysicsTypeDeclarationsReader`. It takes the existing P3 metadata, P4a declarations, source extents and decode steps.

Each record retains its slot and stream identity, parent/subtype links, gated integers, field names, flags, member type links and byte extents. Layout links use the local layout table; interface links use declaration slots. Zero layout targets request no link. Duplicate slots remain distinct records and make references ambiguous. Forward links and cycles remain direct references.

Members expose their original values, native UInt16 projections and effective flags. Raw scalar labels have no assumed units. Stored names come from the FSTR pool, while declaration names remain available through the existing type table. The reader keeps raw byte access explicit and excludes it from ordinary JSON.

A literal zero byte stops native TBDY record traversal. Remaining bytes stay in a skipped range. Unsupported masks, annotation/ASTR and mixed member-order branches retain bounded incomplete ranges. Interface rows have explicit partial profile coverage. Inherited fields are not expanded recursively.

Input identities, revisions, headers and leaf hashes are validated before interpretation. FSTR/TBDY use bounded owned snapshots, with aggregate allocation and cancellation controls. DATA stays with the existing envelope and asset systems.
