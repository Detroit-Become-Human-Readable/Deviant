# IBL light fields

`IblLightStoredFieldsProjection.Project` exposes the fields retained by
`TextureContextReader.ParseIblLightV13`. It uses the existing decoded context and
does not open an archive or resolve resources.

Offsets below are relative to the `IBLLIGHT` class tag. Scalar values retain their
32-bit words and source spans; `GetValue()` interprets a word as an IEEE 754 float.

| Offset | Field | Meaning |
| --- | --- | --- |
| 12 | Enabled | Stored enable byte |
| 13–36 | ExtentBounds | xmin, xmax, ymin, ymax, zmin, zmax; supplies the derived half-extent |
| 37–60 | LocalBoxBounds | Local box limits used for reflection correction, in the same order |
| 61–68 | StoredReference | Native type/id pair; role unassigned |
| 69–72 | AttenuationStart | Start parameter for the influence smoothstep |
| 73–76 | Intensity | Stored intensity multiplier |
| 77–80 | Scalar77 | Unassigned float word |
| 81 | SpecularTexturesEnabled | Stored specular texture gate |
| 82 | Byte82 | Stored byte |
| 83–86 | Scalar83 | Exponent input; property name unassigned |
| 87–90 | TextureReferenceCount | Number of texture pairs |
| 91–94 | Word91 | Unassigned word |
| 95 onward | TextureReferences | Native type/id pairs, eight bytes each |
| After texture pairs | FirstSpecularSetIndex | Stored first-set index |
| After index | OwnerName, SourcePath | Length-prefixed ASCII strings |
| Final eight bytes | TrailingScalar0, TrailingScalar1 | Unassigned float words |

`TryGetBounds` returns finite, ordered bounds, including flat bounds.
`HasPositiveVolume` additionally requires a positive extent on every axis.
Reversed ranges, non-finite values, signed zero and out-of-range set indices retain
their original values. Consumers choose which values their operation supports.
The stored ranges alone do not establish a scene-space influence volume or its
center; those also depend on the instance transforms and shader inputs.

Bound references use the native namespace and the IBL resource as their owner.
Sentinel pairs remain in the stored list and return no bound reference. Resolution
uses the shared resolver, including its missing and ambiguous outcomes.

Field spans refer to the decoded container, which may have a different member path
from the IBL identity. The projection validates their index, ordinal, offsets and
lengths and string digests, and copies the retained arrays and lists. Limits match the context reader:
256 texture pairs, 4,096 bytes per string and a 16 KiB class record. Physical
provenance is limited to 8,192 spans.

The result records a versioned projection step. It does not contain a complete
body digest, an instance transform, runtime texture selection or generated zone
indices. Scene placement and rendering obtain those through their own contracts.
