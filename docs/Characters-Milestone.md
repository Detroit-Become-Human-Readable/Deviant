# Characters workspace

This is the next core milestone. The broader application program remains tracked in `Application-Program.md`.

## Ownership

| Work | Owner | Delivery |
| --- | --- | --- |
| Shared tab, browsing, selection, jobs and release | Application agent | One Characters workspace integrated into the existing shell |
| Character names, model variants and resource relationships | Research agent | Evidence-backed catalog using shared resource identities and naming evidence |
| Actor, skeleton, skin and animation format contracts | Research agent | Exact accepted reader dependencies and supported clip-to-rig mappings |
| Model/rig preview and animation playback | Scene agent | Components using the existing viewport and shared memory budget |
| FBX model and animation export | Scene agent | Export contracts and implementation using decoded models and verified animation data |

The application owner integrates the accepted components and runs the actual combined package checks. No competing shell, character database, decoder or viewport is introduced.

## Current capability assessment

- Current Main has a static scene-linked model viewer, geometry and skin validation, native mesh/material loading and shared resource identities.
- The native geometry decoder currently rejects mesh parts with a nonempty skin palette. Character skin decoding and palette-to-rig resolution are explicit dependencies; generic skin validation alone does not establish a working character preview.
- The accepted actor, body, model-prefix and skeleton readers are in Main. Their current Scene/Model compatibility gate passed 154 cases. The rig viewer and standalone model adapter still require consumer intake and combined host acceptance.
- Current animation resources have record/header/version-14 layout inspection and JSON export. This does not yet provide decoded motion tracks, compatible character playback or animation FBX export.
- Main has a resizable Characters workspace connected to the shared production catalog, exact model contexts and LOD details. Bounded 200-row paging and global search are integrated. The selected package displays 132 real cached entries, including a stored Connor head context; actual native character preview remains blocked by deformation and matrix mapping.
- The shared static FBX writer is in Main with independent geometry readback. It requires verified coordinates and supported geometry; native character skin, materials and animation export remain separate dependencies.
- Workspace admission hides game-dependent navigation until a valid installation is loaded. The selected package passes seven admission checks. The newer source increment moves Characters into the shared left Explorer and removes the visible top-level tab strip.

## First integration dependencies

| Dependency | Current state | Owner/next action |
| --- | --- | --- |
| Exact actor/body/model/rig catalog | Actor dependencies are accepted; catalog binding is being derived from stored names and verified typed references | Research agent supplies typed candidates, variant context and dependency status |
| Standalone model preview | Accepted NativeModelSession snapshot adapter and AssetModelView standalone load are in Main; Characters host uses the same model control and shared budget | Connect the accepted research catalog/binding and verify an actual character in the integrated tab |
| Rig inspection | Accepted component is metadata inspection; matrix ordinals are not paired to nodes | Research/scene agents recover and validate required hierarchy/bind relationships |
| Skin decoding | Nonempty palettes are rejected by current native geometry decoder | Scene agent implements accepted native profile and weight/joint mapping with source evidence |
| FBX writer/readback | Shared static writer accepted with four Main cases and 123 independent numeric comparisons | Scene agent extends accepted materials/skin/motion contracts; application owner connects supported exports |
| Animation | Existing version-14 framing has no timed-track or rig-binding API | Research agent establishes tracks/timing/mapping; scene agent consumes them for preview/export |
| Characters component | Descriptor consumer, model-provider route, explicit cancellation and borrowed-model lifetime pass current Main host checks | Application agent connects the production catalog and verified model/rig binding, then tests the actual combined package |

Existing native model loading uses `NativeTextureSession` and the shared `AssetMemoryBudget`; catalog inputs are type-3 archive identities and mesh inputs are exact native-2130 member identities with an explicit group index. Preserve this route and its ownership.

## Required experience

Browse characters by established names, including Connor, Hank and Kara when the recovered index relationships identify them. Choose model variants without guessing resource numbers. Open the model in a resizable Characters tab, inspect its rig and materials, choose a verified compatible animation, explicitly play/pause/seek it, and export the supported model or animation to FBX.

Inspection must default to the verified highest-detail mesh. Retain a manual LOD override, support automatic LOD switching for heavy scenes, and require an explicit detail choice for export. Reuse the same LOD identities and groups throughout. Stored ordinals and thresholds do not by themselves establish quality ordering or variant meaning.

Use recovered names where available. Keep duplicate identities, variant context and naming evidence; show unresolved associations rather than selecting the first numeric match. A bank name mentioning a character does not establish that a mesh or speaker is that character.

The first vertical slice is one identifiable character and variant, its actual model/materials/rig, and an independently verified FBX export. Expand to the character catalog while motion-track and rig compatibility work proceeds. Playback and animation FBX export remain part of this milestone and are not replaced by static pose or opaque-data export.

## Acceptance

| Scenario | Required proof |
| --- | --- |
| Find Connor, Hank or Kara | Original naming/context evidence and exact indexed identities; variant and ambiguity handling |
| Preview a character | Actual decoded geometry/materials/rig in the integrated tab; selection/cancellation and shared-budget cleanup |
| Select model detail | Verified highest-detail default, explicit manual override, preserved missing/ambiguous levels and scene auto-LOD behavior within the budget |
| Export a model as FBX | Independent readback of hierarchy, geometry, UVs, normals, materials and supported skin/bind information |
| Play a compatible animation | Verified track representation, timing and bone mapping; actual pose changes, pause/seek and model-switch cleanup |
| Export animation FBX | Independent readback of duration, tracks, keys and target skeleton; no fabricated curves |
| Combined release | Exact source/component versions, one frozen package, actual packaged UI checks, screenshots and launcher verification |

Preserve the selected release and pending dialogue/scene work. Keep original game files and saves read-only. No commits or pushes without a user request.

## Current integration checkpoint

The central `artifacts` folder has been intentionally removed by the user and must remain absent. The earlier selected executable hash `3460d7ad312375f2eb27692add7a84473b9e347a904b29a898c937deadfdb97a` is historical. Private candidate launchers are used for review and testing; this integration does not recreate the central folder.

The exact four-path scene-return repair is intaken under manifest SHA-256 `da923741d18584c5c0b18a3287a393ff03c345b9ca9d18da8a25a38f3bd5f471`. It loads structure before restoring the saved render region and restores prior large-data approval after successful state validation. Current host tests pass; actual combined GPU transitions remain required.

The standalone adapter is accepted under component manifest `6c1fac0ba9f4fea7ffe7637ee1f2208e802769da66e82947c740e510370f7ca0`. Current Main passes 78 host/layout/scene cases and five standalone loader cases. A narrow model pane stacks its resizable inspector below the viewport; a wide pane uses a resizable side inspector. Exact current source versions are sealed in `.local/application/Characters-Integration-Acceptance.json`. These generated checks do not supply a retail character catalog, GPU character preview or FBX/animation acceptance.

Research's cluster and blendshape framing is now in Main after 54 composed private cases, the exact retained endpoint and 143 current Main Model cases passed. Stored eyelid labels provide head context. Actual GPU geometry, skin/bind mapping and timed motion remain dependencies. A collision reference naming Connor does not identify a complete character model; Hank and Kara model/variant mappings remain to be established.

The accepted binding reader verified eight LOD selections, 495 stored rig matrices and a unique body relation against one exact owner/catalog, with tracked memory returning to zero. Its corrected Main gate passes 11 cases and retains every catalog companion link while selecting the explicitly leased owner.

The current Characters consumer passes 86 combined Desktop cases. It uses the shared bookmarks/recent library, reopens exact identities and revisions without previewing, rejects duplicate saved identities, and exposes all LOD choices from the accepted binding. Missing/ambiguous choices cannot invoke preview, and changing detail cancels pending work. Technical errors stay in Advanced. Exact versions are in `.local/application/Characters-Consumer-Current-Acceptance.json`.

The first actual highest-detail geometry probe stopped at `NativeMeshPartProfileUnsupported`: 37 parts carry raw profile values 1, 4 and 8, while the current decoder accepts 0. Tracked CPU memory returned to zero; no GPU upload occurred. Profile interpretation, verified coordinate axes/units, skin/bind data, actual character discovery, highest-detail ordering, FBX and motion remain required.

The default production data source is now connected. Actual offscreen current-app checks displayed 132 cached model entries, found stored Connor-head context by indexed name search, opened exact details with eight LODs, reopened the tab and drained asset memory to zero. Coverage is three catalog contexts of 2,649, and no model preview or GPU upload was performed. Source/module evidence and screenshots are recorded in `.local/application/Characters-Production-Acceptance.json` and `Checkpoint-Characters-Production.md`.

The sealed static FBX component is in Main and its four writer cases pass; independent ufbx readback verified 123 numeric comparisons. Native character export still requires actual part-mode/coordinate/skin evidence, with full material/media, morph and timed motion support outstanding. The complete FBX and animation requirements remain unchanged.

Production results now have bounded 200-row pages and exact saved-item queries beyond the prior 1,000-row display limit. The 1,205-entry generated regression includes search reset, last page, exact identity, no preview and memory cleanup. Versioned paging evidence is in `.local/application/Characters-Paging-Acceptance.json`.

The approved cache-resume consumer is now integrated in Main. Explicit Find more validates persisted coverage in cancellable batches, resumes at the first unverified group, and reconciles discovery committed just before cancellation. Sparse gaps are preserved; source replacement clears coverage state and its shared-budget reservation. Seven generated resume/lifecycle cases and the full 407-case Desktop suite pass, with no blocking review findings. Exact source and red/green evidence are in `.local/application/qa/characters-coverage-resume/Source-Acceptance.json`.

A bounded real-data check on current Main also passes: 200 displayed cached identities remain unchanged after eight catalog groups are validated and indexing is cancelled; the discovery cursor remains zero, all 15 loaded modules match, tracked usage is below the 512 MiB budget, and source/cache cleanup releases memory. Evidence is `.local/application/qa/coverage-resume-real-1/Acceptance.json`. This does not claim restoration of all retail coverage. The increment is outside the frozen candidate; packaged coverage acceptance remains pending.

The accepted increments are now frozen in a new private candidate, executable hash `537ad9a0ff68a359fe5f671599a152a4bac243622eea5a29abc68ac6990639ac`. All 619 frozen public files and both archives were verified, and 407 Desktop cases pass against both frozen source and actual packaged product assemblies. The bounded real-data coverage/cancellation check also passes on this exact package with 15 matching loaded modules. Dialogue and scene acceptance on the new candidate remain in progress; older candidate receipts are kept separate. Full retail coverage, actual character preview, rigged FBX and animation remain open.

## Combined candidate and native pose intake

The frozen private combined candidate has executable SHA-256 `121ce403fe4eb0a436ad8b2e01120989fdad465d26c6f2297dad7f643d2448b1`. Its source and packaged-assembly Desktop suites each passed 400 cases. Seven installation-admission cases were rerun against its packaged assemblies: navigation stays hidden before validation, invalid inputs are rejected, valid inputs reveal it, and replacement/cache transitions preserve the admission boundary. These are programmatic WPF checks.

Actual candidate checks now pass nine dialogue/playback/export tasks and five Characters browsing tasks using the shared QA cache. Original game-text search and forward/reverse audio navigation preserve the selected occurrence; muted playback advances, pauses and seeks; Original and WAV exports pass independent readback; cleanup drains sessions and tracked memory. Characters displays a bounded 200-row page, finds stored Connor head contexts, opens one exact context with eight LOD choices, and reopens without creating a preview. All loaded product modules match the candidate. Screenshots and receipts are under `.local/application/qa/combined121-dialogue` and `.local/application/qa/combined121-characters`. Physical interaction and audible listening were not performed. Repeated scene/model transitions on this candidate remain a release gate; older candidate receipts are kept separate.

The position evaluator replacement 2 is integrated with source, frame and full matrix-count validation. The research-approved vector component `native-pose-vectors-current-1`, manifest SHA-256 `7916de81fdd635d53152625ce383f4922c391d1fc617dbdb8a1ec93dcd542b3b`, is now integrated through five hash-fenced source paths. It shares weighted rows with the position evaluator and implements the recovered packed normal/tangent rules for modes 1, 4 and 8. Its Main gate passed 70 cases: 29 position/vector cases and 41 retained attribute/geometry cases, with no failures or skips. Exact source hashes and the test receipt are recorded in `.local/application/qa/native-pose-vectors-current/Main-Acceptance.json`. The frozen candidate is unchanged.

The captured 438-matrix pose has not been associated with the indexed 495-matrix rig. Character preview admission, hierarchy/bind mapping, coordinate basis, morphs and compatible timed animation remain open. Neither pose math nor FBX writer tests establish a complete character export or animated preview.

The 438-row menu skeleton now has its own exact source join: native 2013/410 in container 29/3161 resolves to one archive row at ordinal 368139 and member `dc-object/105/2013/410`. The application owner verified all retained input hashes and reran the retained-only verifier; 28,032 converted matrix bytes match both saved live frames exactly. This join is separate from the 495-row frame. Its source hierarchy contains 438 nodes and 83 exact stored-name-key overlaps, with 355 unmatched keys; joint registry, general owner factor and pose-space semantics remain pending. The scene owner is taking the existing shared resolver/model/rig readers through the first model slice; no new decoder or mesh/bind admission is inferred. Receipt: `.local/application/qa/Character-438-Source-Join-Intake.json`.

The 495-row source now also resolves independently: outer asset 29/3343 at ordinal 41096, nested member `dc-object/552/2013/429`. The application owner checked the retained payload hash, count/extent, source ID and 31,680 converted matrix bytes against the saved live table; the bytes match exactly. Receipt: `.local/application/qa/Character-495-Source-Join-Intake.json`. This supersedes count-based or near-equal candidate selection. It does not yet establish the displayed character's name, mesh/rig/bind mapping, general owner factor, coordinate basis or animation compatibility; shared production contracts and renderer admission remain with the research and scene owners.

The shared skeleton/node-array binding adapter is accepted in Main, with explicit revision, typed ENTITY reference, member identities, extents, hierarchy and exact-key name joins. Its ten focused cases pass. The real retained consumer replay also passes for both sources: 438 entries with 83 unique name matches and 355 missing keys, and 495 entries with 133 unique matches and 362 missing keys. All 933 ordinals, parent/child backlinks, name keys and six loaded assembly hashes were independently verified. Borrowed source snapshots remain retained throughout consumption; peak tracked usage is 25,599,507 bytes and final usage is zero. Receipt: `.local/application/qa/skeleton-node-binding-current/Real-Consumer-Intake.json`. This is source-binding acceptance, outside private candidate537; renderer, general owner factor, bind/export and animation admissions remain open.

Main Desktop now builds with the accepted binding dependency: Debug build exits zero with no warnings or errors, and its copied Models DLL matches the accepted assembly SHA-256 `f0de2abe0d0e202656ce0b9332cfb769d3306eee5f2fb2712c65aab0d68e556e`. Publishing was disabled for this check. The selected private candidate is unchanged and the central artifacts folder remains absent. Exact source hashes and the build receipt are in `.local/application/qa/skeleton-node-binding-desktop/Acceptance.json`.

The on-demand `NativeSkeletonBindingSession` source and focused lifecycle gate are now intaken under owner receipt SHA-256 `b621029c5b498693ef0dab39ab370dab47e9064b7169a50d11f78604dd6bda21`. All four delivered source hashes match and the final test report confirms eight passes. These cases cover independent consumer retention after owner disposal, caller-owned container retention, multiple-consumer cleanup, missing/ambiguous references, revision/group mismatch, cancellation, changed-source rejection and budget-pressure rejection before parsing. Parser limits fit an explicit reservation; oversized estimates are rejected before allocation. Receipt: `.local/application/qa/Skeleton-Binding-Session-Source-Intake.json`. Real 438/495 session acceptance is recorded below. New Desktop inspector wiring and packaged acceptance remain pending; the earlier adapter replay was not used as a replacement for session checks.

The accepted session dependency also compiles through Main Desktop. Six existing Characters binding-host lifecycle cases pass with source hashes unchanged; the built Browser DLL and Desktop copy both have SHA-256 `c3915644315892a11d97de6a480c41d4e6623e47a8a41f0ed259b63da1c78f51`. Automatic publishing was disabled, the selected private candidate is unchanged and central artifacts remains absent. Receipt: `.local/application/qa/skeleton-binding-session-desktop/Acceptance.json`. These existing host tests do not establish new inspector wiring, real session consumption or packaged acceptance.

The exact 495-source model inputs are now intaken separately. Eight retained mesh metadata files match unique source-directory members and their hashes. Within this source, serialized detail level zero has the highest vertex and index counts: 50,788 vertices, 214,896 indices and 41 parts. Its retained GPU input is 3,995,488 bytes and matches the capture hash. The application intake performed no original game reads. Receipt: `.local/application/qa/Character-495-Geometry-Inputs-Intake.json`. The scene owner is checking neutral skin attributes through the existing reader/decoder; posed geometry, coordinate basis, bind information and character rendering remain open.

The actual shared skin-attribute consumer produced 24 buffer outputs containing 50,447 vertices and 395,504 active influences. Application checks cover every exported influence record: active offsets are multiples of three within the exact 495-entry domain, the maximum row offset is 1,482, stored weights are finite and nonnegative, and active weight sums are positive where normalization applies. Output lengths/counts and four loaded assembly hashes match; peak tracked usage is 7,264,410 bytes and final usage is zero. Receipt: `.local/application/qa/Character-495-Skin-Output-Intake.json`. The scene owner's independent stored-byte comparison also passes for all 395,816 stored lanes, with exact packed weights and row values. Its receipt SHA-256 is `8503bf2a23c304cedb6fdb399b9c84a7439d0b73ed26d20c9ea2e628cd132cdf`. These checks do not accept posed geometry or rigged FBX.

The naming audit also explains the current discovery limit. All 271 cached model entries use legacy v2 metadata, with no current validated catalog-coverage checkpoints. Their labels include two Connor entries and one Alice entry; the remaining 268 are generic. No cached Hank or Kara name match was found. This does not establish that their models are absent from the game. Research's bounded read-only receipt is `.local/format-program/research/characters-current/coverage-audit/retained-name-coverage-receipt.json`, SHA-256 `d432a19eccafa45260ec675925388f62b7f715a8ae7917bf9a7a3d26ff95d859`. Further discovery must upgrade metadata and establish coverage checkpoints rather than infer completed visitation from the number of cached models.

The scene owner's binary FBX/embedded PNG component is integrated through four hash-fenced paths. Current Main passes 18 exporter cases and six shared snapshot coordinator cases. Independently verified receipts cover 320 material/media, 164 geometry and 20 footer comparisons across four files. ASCII with texture sidecars remains the default. Source validation and coordinate/geometry admission are preserved; skin, morph and animation export remain open. This component is outside the frozen combined candidate.

The frozen candidate also passed 21 real-bank layout checks at 2560×1392 and 1000×660, with 12 accepted naming labels and 15 matching product modules. Three actual scene navigation cycles, reload cancellation and cleanup pass on this same candidate. Independently matched evidence covers 17 modules and 15 screenshots; six restored scene captures are identical, tracked memory stays within the 512 MiB budget, and tracked CPU/GPU usage returns to zero on close.

Automated Windows mouse/keyboard input now verifies installation admission, Characters search/selection, Audio navigation and all four resize handles. Screenshot boundaries and 15 loaded product modules were independently checked. Normal keyboard closure releases sessions and tracked memory. This uses Windows SendInput; it is not a human-operated hardware test. The compact-window layouts remain covered by separate WPF probes. The bounded application acceptance is closed; native character rendering, rigged FBX and animation requirements remain open.


The actual retained-session component is now accepted after independent application intake. Both exact catalog/container/model paths return the expected 438/495 source joins. All 933 entries match source-verified hierarchy, descriptor keys, matrix extents and name ordinal/key identities; converted matrix hashes match the retained native tables. Eight loaded assemblies and sealed source hashes match. Consumers stay valid after both owner disposals, new acquisitions are rejected, and final release drains tracked memory to zero. Peak tracked usage is 484,237,855 bytes, below the shared 512 MiB limit. Receipt: `.local/application/qa/Skeleton-Binding-Session-Real-Intake.json`. Inspector/renderer integration, posed geometry, bind/basis, rigged FBX, animation and combined package acceptance remain open.

Detached current-version discovery is also intaken for the two qualified catalogs. It returns 58 unique model entries and four stored blendshape labels supporting two derived Chloe/Connor head-context names. Exact catalog/owner links, naming hashes and eight loaded modules match; three mesh-metadata issues remain explicit. Peak tracked usage is 326,141,328 bytes and final usage is zero. Receipt: `.local/application/qa/Characters-Detached-Discovery-Intake.json`. This covers two of 2,649 catalogs in private evidence only: no persistent checkpoints, cache changes or package changes were made, and no complete character assembly is accepted.

The descriptor-retention repair is now accepted in Main and intaken by application. Both delivered source hashes match, all 219 Model tests pass without skips, and the new Models DLL has SHA-256 `fa8afa1ec7cbbd9dd7fada632e16a1a1a9f7aa8f391538b7ed006593d301679f`. A second detached pass preserves all 58 identities, clears the three metadata errors and increases entries with naming evidence from two to five. Geometry decoding remains unchanged and unsupported stream/profile rendering stays rejected. Receipt: `.local/application/qa/Vertex-Descriptor-Main-Intake.json`. No cache or selected-package changes were made. Research is auditing the metadata cache-version boundary before combined Desktop dependency acceptance; the current v3 contract must not silently reuse metadata rejected by the earlier reader.

