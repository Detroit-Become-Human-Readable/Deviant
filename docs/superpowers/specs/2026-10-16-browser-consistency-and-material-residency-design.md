# Texture browser consistency and scene material residency

Status: proposed specification, awaiting review.

## Purpose and scope

Make texture browsing, saved filters, navigation and closing reliable while indexing runs. Then let scenes retain the materials needed for the current view within the existing graphics memory limit. Keep the current C# and WPF application, source-derived material behavior, geometry identities and requested final detail.

The approved direction has two ordered work packages: browser consistency, then material residency. This document defines their shared lifetime boundaries and acceptance criteria. Implementation and an implementation plan follow review of this specification.

The source checkpoint is commit `93263ec`. Its Release build succeeded. The broader desktop run reported failures in `AudioUpdatesCannotOverwriteTheVisibleBoundedCatalogState` and `SavedCatalogKeyReadsRequestedGroupBeforeGlobalDiscoveryCompletes`, followed by an aborted host. Focused backing, upload and Records tests passed. Those successes do not establish application acceptance.

The latest private scene replay completed backend preparation but failed during final material acquisition at the graphics admission limit. CPU backing reduced retained pixel demand; it did not solve GPU residency or meet the scene timing gates. Exact retail workloads, logs, package identities and samples remain in private diagnostics.

Excluded: a renderer rewrite, new formats, missing-name research, audio redesign, animation playback, clip-to-rig mapping and mod-pack management. Existing material interpretation gaps remain separately tracked. This work must preserve their diagnostics rather than relabel unsupported content as complete.

## Observed failure paths

- `FeatureStore.TextureReads.cs` can exhaust four attempts to read changing projection state and throw `TextureProjectionGenerationChanged`.
- `AudioWindow.TextureCatalogs.cs` observes that refresh during cleanup but handles only cancellation at that boundary. A failed refresh can interrupt subsequent disposal.
- `TextureExplorerView.xaml.cs` reconstructs queries and saved filters from dropdown selections. Updating available options can replace an absent catalog selection with the unrestricted option.
- Saved-filter navigation already supports direct catalog lookup. The repair must preserve that path and make the requested key survive subsequent option updates.
- `AssetViewport.Streaming.cs` completes every part's material before finishing the scene. Material leases accumulate in the viewport's lifetime list. `Part` owns a geometry lease but has no explicit material binding lease.
- Current visibility changes filter instances; they do not release material demand. Completing a streamed scene also acquires replacement materials while old material leases remain retained.

These observations identify paths to repair. Deterministic tests must establish the precise ordering behind each desktop failure before changing its implementation.

## 1. Consistent texture browsing

### Published generations

Use the existing versioned texture projections and history indexes. Extend their read lease into an explicit logical generation pin identified by source revision, projection profile and published generation. Publication means all scalar rows, catalog membership, roles, counts and required indexes for that generation are transactionally queryable.

The foreground reader opens a short read-only connection and transaction against that pinned generation. One response contains its page, matched count, indexed coverage and filter options, with the same generation identifier. Subsequent pages keep the pin until the query changes or the user accepts a replacement result. The lease retains logical history, not an open SQLite transaction across user interactions.

Writers prepare bounded dirty-key changes and publish the new head in a short transaction. Readers do not perform history rebuilding or role preparation. History needed by an active pin cannot be pruned. Releasing or replacing a browse session releases its pin. Source replacement invalidates the session independently of ordinary indexing progress.

Where the existing current-table fast path cannot serve a pin consistently, use the indexed historical representation. Do not expose an incomplete head and compensate with larger retry loops. The first indexed batch can be a small complete published generation; browsing does not require completion of global discovery.

### Authoritative query state

Keep a typed query state separate from WPF controls. It contains search, dimensions, format, role, relationship filter, kind, sort, owner/catalog keys and page position. The controls display that state. `CaptureFilter` serializes it; background option updates cannot change it.

Applying a saved filter validates its syntax and source identity, resolves a requested catalog directly, and publishes that catalog's projection and checkpoint before issuing its filtered page. It does not wait for unrelated discovery. The requested key stays selected even if a previously fetched options list does not contain it.

While resolution is pending, preserve the requested key and show loading. A verified missing or invalid catalog produces an unavailable result with that key retained. It must never fall back to an unrestricted query. Other unavailable saved facets receive the same treatment. A deliberate user selection of the unrestricted option clears the key.

### Refresh and cancellation

Use one foreground request owner per view, carrying workspace identity, navigation generation and query version. Refreshes coalesce into at most one running request and one pending replacement. A new query or navigation cancels obsolete work; an obsolete completion cannot update rows, options, selection or status.

A replacement response publishes its options, counts and page together on the dispatcher. Keep the last valid response visible until that response is ready. Selection is preserved by resource identity when present in the replacement. If the new result excludes it, clear selection explicitly without changing the query.

Index advancement is normal and does not become a fatal browser error. Corruption, source replacement and database failures remain distinct. A corrupt row is isolated and repaired through a writer transaction; no reader publishes unverifiable evidence or initiates whole-cache restoration.

### Closing and cleanup

Closing first cancels timers and request owners, then drains their tasks, then disposes sessions and connections. Drain code records task failures and still runs all remaining cleanup in `finally` paths. Expected cancellation does not appear as a failure. Real errors retain the operation, error code, affected identity and exception in diagnostics.

An `async void` closing handler must contain its exceptions and finish its close protocol. It must not abandon cleanup because a previous refresh failed. Ordinary navigation uses the same cancellation and stale-result rules.

### Components affected

- Catalog: texture generation publication, history retention and projection read leases.
- Browser: `TextureDiscoverySession` generation-bound page/options responses and direct catalog preparation.
- Desktop: `TextureExplorerView` query state, texture refresh coordination, saved-filter application and close/drain boundaries.

Keep existing public callers compatible through adapters where needed. The desktop's production browse path uses the generation-bound response; adapters must not combine independently captured generations.

## 2. Visible material residency

### Ownership and descriptors

Separate material description from resource acquisition. A prepared descriptor identifies source revision, shader/render state, texture content and sampling keys, required UV/tangent layout, and estimated unique upload sizes. Descriptor creation preserves existing material decisions and does not hydrate texture pixels or allocate GPU textures.

Each drawable part owns an explicit material binding lease. Shared material and texture pools still deduplicate resources; a material is live only while an active binding or an in-flight operation owns it. Stream completion updates descriptors and bindings instead of appending another permanent set of material leases. Geometry, transforms, occurrence identifiers and selection remain available independently.

Changing material technique or geometry layout replaces the affected render model atomically using the descriptor's required UV/normal layout. A replacement cannot leave a model using geometry prepared for a different material contract.

### Demand selection

Compute demand from current world bounds and the rendered camera's view/projection frustum. Source/manual visibility remains authoritative. Selected parts receive first priority, followed by visible parts ordered by projected importance and distance. Use stable identities for ties. Parts with unknown bounds are treated conservatively rather than silently discarded.

Retain a small visibility margin and recently used bindings when there is room. Those bindings are evicted before selected or visible demand. The margin is a tuning parameter measured on the retained workload, not an additional memory allowance.

Camera movement, selection, manual visibility and streamed scene changes mark demand dirty. Coalesce them into one residency operation; do not start an operation for every input event. Pixel preparation runs off the dispatcher. Model binding and GPU submission remain serialized through the renderer owner. All work carries navigation, snapshot and device generations.

### Admission and retirement

Account geometry, instance buffers, textures, pending uploads and retired allocations against the existing graphics budget. Estimate incremental texture demand by unique pool key, including resources shared between candidate materials. Reserve CPU scratch and GPU bytes before dispatch; actual allocation sizes reconcile with the reservation.

Replacement follows this order:

1. Detach bindings selected for eviction and submit that change through the renderer.
2. Retain their leases and reservations until a renderer retirement ticket completes.
3. Release retired material/texture leases, then admit and bind replacement resources.

The retirement ticket must cover GPU use of the detached resources. A dispatcher yield or a rendered-event notification alone is insufficient to establish this. For the current D3D11 renderer, insert an event query on the owning render context after the detach frame's commands and observe completion asynchronously. Polling cannot block the dispatcher or read the immediate context from an unowned thread. Device teardown can complete retirement only after the old context/resources are no longer usable.

A hidden or minimized viewport stops replacement uploads and keeps pending retirements owned until rendering resumes or the device is safely torn down. It does not treat the existing dispatcher-only frame-wait fallback as retirement. Cancellation prevents new admission and stale binding, while a renderer-owned retirement queue finishes releasing resources already detached. The queue has a bounded memory footprint because retired allocations remain charged to admission.

Cancellation drains foreground preparation, reads and submission tasks within two seconds; it does not claim that an allocation is reusable before retirement. Pending retirement is represented as owned state rather than a blocked foreground task, and its bytes remain visible in diagnostics. Closing or device teardown must also release that state. A renderer that cannot retire or tear down within the cleanup deadline produces an explicit cleanup failure instead of a successful drain result.

### Pressure and failures

When the visible working set exceeds the budget, retain the highest-priority materials that fit. Geometry remains selectable. Pending parts use an explicit loading representation that does not masquerade as the source material. Where a placeholder cannot safely participate in the source depth/blend pass, retain selection through bounds/outline visualization until its material is ready.

Supported preview mips may be used during preparation, with the requested final detail retained as the target. Do not introduce arbitrary recompression or permanently lower export detail. If final visible demand still cannot fit, report `Texture budget reached`; do not announce fully textured completion. An individually oversized material is reported without an automatic retry loop.

Recoverable material failures stay attached to the affected part. Previously loaded geometry and selection survive. Full exceptions remain in diagnostics; short UI status distinguishes loading, memory pressure, unsupported content and failure. No blanket transparency change is included.

## 3. Verification and delivery

### Browser checks

Use generated archives and deterministic barriers to force publication during option/page reads, refresh completion after reselection, navigation during direct catalog resolution, and closing after a refresh fault. Verify one generation across options/counts/pages, stable paging, preserved catalog keys and complete disposal. Cover interrupted indexing, a corrupt individual entry, source replacement and existing caches.

Run both previously failing workflow tests, the full desktop suite and affected catalog/browser regressions. Any aborted test host remains a failed acceptance run. Reproduce the workflows in the exact private packaged UI, including saved filters, search, paging, rapid selection, Home, reopening and closing.

Cached pages/search and cancellation acknowledgement must be at most 250 ms. Cancelled work must drain within two seconds. Measure dispatcher heartbeat independently of backend duration. Supported prepared texture preview remains at most two seconds.

### Residency checks

Use a simulated allocator and controllable retirement tickets to verify shared-texture accounting, detach-before-release, no admission before retirement, cancellation during upload, stale device generations, oversized materials, hidden viewports and zero retained ownership after teardown. Verify the same priorities at identical camera/selection state regardless of discovery order.

Then exercise the real GPU in a frozen private package: load the retained scene, move across disjoint views, select objects, change visibility, return to the scene, recreate the device and close. Record live and retired GPU bytes, unique texture counts, uploads, evictions, queue waits, frame pacing and cleanup. Compare representative opaque, cutout, transparent and character materials before and after binding replacement.

Rerun the exact private all-region workload with fresh application-cache, prepared-cache and in-session cases clearly separated. Require a usable cold scene and a fully textured prepared view within ten seconds across three consecutive runs. Use fixed camera/interaction scripts and expected visible identities so a single early object cannot satisfy usability. Record full content coverage and full cold completion separately. Content omissions or unresolved required materials fail final-fidelity acceptance.

Fully textured completion requires complete requested scene coverage, resolved final material descriptors, prepared final texture data for every required dependency, and final bindings for all expected visible parts in the fixed view. Offscreen GPU eviction does not remove those dependencies or their prepared data. Completion must not be reported merely because a small admitted subset is textured. The subsequent scripted camera traversal checks that additional views acquire their correct final materials without restarting scene loading.

Prepared all-region export retains the two-minute target and explicit requested detail. Browser and residency work does not alter the export's source verification or selected content. Compare geometry, UVs, texture pixels, alpha/color space and independent rigged FBX readback through the broader goal's existing regressions.

### Package and continuation

The lead owns integration, compilation, desktop input and GPU verification in the existing main checkout. Use one working copy and preserve the normal build, library data and prior private evidence. Public regressions contain generated fixtures; private workload identifiers and machine paths stay outside public source and documentation.

Keep the 20 GB disk cache setting separate from CPU admission, GPU admission and process RSS. Preserve the existing limits, including adapter-dependent graphics budgeting. Any speed claim refers to a named exact package and measured workload.

Deliver a private package with hashes, reproducible checks, before/after results and remaining limitations. Review of this specification permits writing the implementation plan; it does not promote a package or authorize a new public release.
