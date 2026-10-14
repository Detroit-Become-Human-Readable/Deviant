# Physics type declarations

`PhysicsTypeDeclarationsReader` reads the TNA1 declaration table from PHYSYS v5 metadata produced by `PhysicsTaggedMetadataReader`. It uses the same resource identity, source revision, source extents and decode steps.

```csharp
var declarations = new PhysicsTypeDeclarationsReader().Read(
    metadata.Value!, metadata.Extents, metadata.Steps, token: cancellationToken);
```

Each declaration has a table slot, name, parameters and byte extents. Its identity combines the owner resource, TYPE chunk ordinal and slot. Slot0 is the table's initial entry; its concrete meaning is unspecified. Declared records start at slot1.

TSTR indexes select nonempty NUL-terminated strings. Negative signed32 name indexes omit the declaration name. A parameter name beginning with the raw byte `t` makes its value a reference to another slot in this table. Other parameters retain a64-bit numeric value. Forward references and cycles remain direct slot references.

The reader preserves the original integer encoding, raw64-bit value and native signed32 projection. Nonminimal encodings are accepted. Names retain raw bytes and optional printable text. Raw byte properties are excluded from ordinary JSON serialization and remain accessible through their explicit properties.

The result reports declaration coverage, diagnostics, retained-memory accounting and unassigned ranges. Extra TNA1 bytes remain in those ranges. Bounds and aggregate budgets apply before allocation, and cancellation is checked during traversal. TSTR/TNA1 bytes are owned snapshots; DATA remains with the existing envelope/asset systems.

This reader exposes type declarations and table references. TBDY field layouts, ITEM/PTCH associations, placed objects and navigation use their own format profiles.
