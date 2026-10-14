# Indexed embedded metadata

`PhysicsIndexedMetadataReader` reuses P3 tagged framing and the P4a/P4b declaration and layout identities. It copies bounded ITEM/PTCH leaves, retains their hashes and extents, and leaves DATA as an extent.

ITEM rows use a native 12-byte stride. Each row retains its three raw words and four-byte field extents. Native enumeration skips row 0; the reader marks it `ReservedRow` and preserves its bytes without projecting its contents.

For supported kinds 1 through 5, word 0's low 24 bits address an owner-scoped declaration slot. Slot 0 is the preexisting declaration slot and has no P4b layout row. A type slot and its optional layout row have separate statuses. Kind 0 uses its signed word 2 as an optional single-hop ITEM row reference; zero means no redirect. It is a different reference from a type slot.

Layout links must agree with the encoded slot values and record order. A terminal zero byte preserves the layout parser's recorded tail. Omitted or reordered records leave lookups unresolved.

Kinds 1 through 3 expose a DATA-relative byte position from word 1, except kind 2 with word 2 equal to zero has no DATA pointer. Positions inside DATA, exactly at its end, and outside its extent are distinct. The reader does not dereference DATA or infer an object size. Kinds 4 and 5 retain raw descriptor fields, and kinds 6 through 15 remain unsupported projections.

PTCH remains an opaque leaf because the supported loader skips it. Raw byte properties stay out of ordinary JSON. Partial tables never establish `Missing` or `Unique`; already observed duplicate candidates remain `Ambiguous`, while a single known candidate stays `Unresolved` with that candidate preserved.
