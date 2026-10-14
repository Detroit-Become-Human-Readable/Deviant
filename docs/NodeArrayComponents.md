# NODEAR component accessors

`NodeArrayComponentSemantics` names the values already exposed by `NodeArrayStoredComponents`:

```csharp
var quaternion = entry.Components.QuaternionXyzw();
var translation = entry.Components.Translation();
var basisScale = entry.Components.BasisScale();
var translationBytes = entry.Components.TranslationStoredExtent();
```

| Accessor | Stored bytes within the forty-byte component span | Native component order |
|---|---|---|
| `QuaternionXyzw()` | 0–15, stored as w/x/y/z | x/y/z/w |
| `Translation()` | 16–27 | x/y/z |
| `BasisScale()` | 28–39 | x/y/z, at native offset32 |

`QuaternionStoredExtent()`, `TranslationStoredExtent()` and `BasisScaleStoredExtent()` return these ranges in the existing decoded-container coordinates. They preserve the component extent's container identity and reject mismatched or overflowing spans. Resource identity and revision remain on the owning `NativeNodeArrayBodyDocument`; physical source extents and decoder steps remain on its `DecodeResult`.

The names follow the version14 input consumers: `5F9560` orders quaternion words; `5FEF10` reads the two vectors; `5F1B20` converts the quaternion into three basis vectors; `3C0390` scales those vectors and places translation in the fourth matrix vector. These RVAs refer to executable SHA-256 `ecf52321921387e683904e089082d76b973326fc093af14e524056715519c1cf`. The supported resource profile is native2138, NODEARv4 with embedded body version14.

The methods delegate to the existing value accessors and preserve values as stored. Raw bits remain available through `GetBits()`. Quaternion normalization, matrix-space labels, physical length units and global coordinate conversions remain separate caller decisions grounded in their own evidence. The methods add no properties to implicit JSON serialization.
