# Visible Material Residency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Load and navigate scenes within the existing graphics budget by owning material resources according to selected and visible demand.

**Architecture:** Describe material requirements without acquiring textures, rank part bindings by camera/selection demand, and admit their unique resources through the existing pools. Evicted bindings remain charged until a renderer-owned D3D11 retirement ticket completes. Geometry and selection survive pressure or a material failure.

**Tech Stack:** C#, .NET 10, WPF, HelixToolkit 3.1.2, SharpDX/D3D11, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-16-browser-consistency-and-material-residency-design.md`, sections 2 and 3. Execute after `2026-10-16-texture-browser-consistency.md` passes its browser acceptance.

## Global Constraints

- Lead implements/integrates on existing main and exclusively owns compiler, GPU, desktop input and any game attachment. No additional working copies.
- Preserve existing CPU/GPU admission limits, including adapter-dependent graphics budgeting. The 20 GB disk cache is a separate limit. Account process RSS separately.
- Keep source pixels, color space, alpha, UVs, source render states and requested final detail. Preview mip use must be supported and temporary. Export content/detail remains explicit and independent of visibility.
- Existing opaque/cutout/transparent/depth interpretation is preserved. Unsupported materials remain diagnosed; no blanket transparency or lossy recompression workaround.
- Public tests use generated fixtures. Private workloads, hardware identities and exact packages remain in ignored diagnostics. No normal promotion, new push or public release.
- Cancellation acknowledgement: at most 250 ms; foreground tasks drain within two seconds. Retired bytes remain charged until GPU retirement. Failure to retire/tear down is a cleanup failure.
- Require usable cold and fully textured prepared scene within ten seconds across three consecutive exact-package runs. Prepared all-region export target: two minutes. Full cold completion is measured separately.

## Review Focus

1. Materials share a texture but use different sampling/color-space contracts: dedup only compatible pool keys and never release another binding's owner (Tasks 1–2).
2. A selected material alone is larger than the budget: report pressure once, retain selection and avoid a retry loop (Tasks 2, 4).
3. The viewport hides or the device changes during eviction: retain ownership until a valid retirement or teardown (Tasks 3–4).
4. A streamed final snapshot changes technique/UV requirements: replace model and geometry together without losing occurrence IDs or selection (Tasks 1, 4).
5. Camera changes repeatedly while uploads finish: coalesce requests and reject stale binding commits without leaks (Tasks 2, 4).

## File structure and commands

Add focused descriptor, planning and retirement files. Keep renderer calls in the renderer-owned bridge; pure demand selection must run without a GPU. Integrate ownership through `Part`, not another scene-lifetime material list. Use task-scoped local commits after red/green checks and review; preserve unrelated changes.

Use `dotnet test tests/Deviant.Desktop.Tests/Deviant.Desktop.Tests.csproj -c Release -p:GenerateDesktopArtifacts=false -p:UseSharedCompilation=false --blame-hang-timeout 30s --filter <task filter>` for generated tests. A host abort fails verification. Record private logs, package pins and all real-GPU process identities. Do not run the known failing scene again until a relevant residency change is integrated.

### Task 1: Describe material requirements without acquiring resources

**Files:** Create `src/Deviant.Desktop/ViewportMaterialDescriptor.cs`, `tests/Deviant.Desktop.Tests/ViewportMaterialDescriptorTests.cs`; modify `ViewportResources.cs`, `ViewportMaterials.cs`, `ViewportSurfaceResources.cs`, `ViewportTextureCache.cs` in Desktop.

**Interfaces:**

- Internal `MaterialTextureRequirement(TextureContentKey Key, long CpuUploadBytes, long GpuBytes)` records one unique texture contract.
- Internal `ViewportMaterialDescriptor(string Key, int UvSet, bool NormalMapped, bool Transparent, bool RequiresSceneDepth, CompiledMaterialGraph? Program, IReadOnlyList<MaterialTextureRequirement> Textures, IReadOnlyList<string> Diagnostics)` records requirements without live resources. The existing `CompiledMaterialGraph` is the bounded CPU expression output, not D3D bytecode.
- Add `AssetViewportService.DescribeMaterialAsync(RenderAssetSnapshot snapshot, RenderMaterial? source, MeshPrimitive primitive, CancellationToken token)` returning `Task<ViewportMaterialDescriptor>` and overload `AcquireMaterialAsync(RenderAssetSnapshot snapshot, RenderMaterial? source, MeshPrimitive primitive, ViewportMaterialDescriptor descriptor, CancellationToken token)` returning `Task<MaterialLease>`.
- Keep the existing acquisition entry point as a descriptor-producing adapter. Descriptor/acquisition must share the same graph-slot/layout decision code; shader compilation and pixel acquisition are deferred to admitted preparation.

- [ ] Add `DescriptorDoesNotAcquirePixelsOrGpuBytes`, `DescriptorMatchesAcquiredLayoutAndState`, and `SharedTextureRequirementsRespectEncodedFormatAndColorSpace`. Assert zero pixel-source acquisitions during description; exact UV/tangent/render-state parity; compatible keys deduplicate while incompatible keys remain distinct.
- [ ] Run descriptor tests red. Factor descriptor creation from existing graph/normal selection without substituting guesses for unsupported state. Run graph conversion off the dispatcher and retain its bounded result in the descriptor so admission does not repeat it. Calculate unique encoded/RGBA/cube sizes using validated metadata and retain complete source revisions in keys.
- [ ] Run descriptor, material, material-state, scene-depth and backed-upload tests green. Commit the reviewed descriptor separation.

### Task 2: Plan deterministic selected/visible demand and incremental admission

**Files:** Create `src/Deviant.Desktop/ViewportResidencyPlanner.cs`, `tests/Deviant.Desktop.Tests/ViewportResidencyPlannerTests.cs`.

**Interfaces:**

- Internal `MaterialResidencyCandidate(string PartKey, ViewportMaterialDescriptor Descriptor, bool Selected, bool Visible, bool RetainedMargin, double ProjectedImportance, double Distance)`.
- Internal `MaterialResidencyPlan(IReadOnlyList<string> Admit, IReadOnlyList<string> Evict, IReadOnlyList<string> Pending, IReadOnlyList<string> Oversized)`.
- Pure `ViewportResidencyPlanner.Plan(IReadOnlyList<MaterialResidencyCandidate> candidates, IReadOnlySet<string> residentPartKeys, IReadOnlySet<TextureContentKey> residentTextureKeys, long maximumMaterialGpuBytes, long availableGpuBytes)` returns the plan. Maximum material capacity is the existing device limit minus nonmaterial allocations; currently available bytes exclude every live allocation, upload and pending retirement. A descriptor is oversized only if it cannot fit the maximum material capacity, not merely because current free bytes are low. Eviction is only a proposal; it does not make bytes immediately available. Replan after confirmed retirement.

- [ ] Add `SelectionWinsAndOrderingIsStable`, `SharedTextureBytesAreCountedOnce`, `OffscreenMarginYieldsBeforeVisibleDemand`, and `OversizedMaterialDoesNotEvictEverythingOrSpin`. Use small generated byte budgets with exact assertions on admitted/evicted keys and total unique bytes.
- [ ] Run red, then implement stable selection-first/visibility/projected-importance/distance/key ordering. Use a 10% horizontal/vertical clip-space visibility margin, with near/far planes unchanged, as the initial tuning value to measure; it adds no memory entitlement. Preserve existing resident bindings when demand priority ties to reduce churn. Treat invalid bounds conservatively as visible and normalize nonfinite importance/distance to conservative stable values.
- [ ] Run all planner tests, including reordered input yielding identical output and disjoint-camera demand. Commit the pure planner.

### Task 3: Retire detached GPU resources before releasing reservations

**Files:** Create `src/Deviant.Desktop/ViewportGpuRetirement.cs`, `src/Deviant.Desktop/ViewportRetirementRenderHost.cs`, `tests/Deviant.Desktop.Tests/ViewportGpuRetirementTests.cs`; modify `ViewportResources.cs` and renderer-host initialization in `AssetViewport.xaml.cs`.

**Interfaces:**

- Internal `GpuRetirementTicket(long DeviceGeneration, Task Completion)` represents GPU completion after detached bindings have traversed the renderer.
- Internal `IGpuRetirementBackend` supplies `GpuRetirementTicket Submit(long deviceGeneration)` and `void DeviceDestroyed(long deviceGeneration)`; production implementation queues an event query for the application-owned render-host override, after the base render has submitted its command lists. It never touches the immediate context from the dispatcher or a worker.
- `ViewportGpuRetirementQueue.Retire(IDisposable resource, long bytes, GpuRetirementTicket ticket)` retains the resource and accounting. `Task DrainAsync(CancellationToken token)` waits without blocking input; teardown completes only tickets for the actually destroyed device generation.

- [ ] Add `RenderedNotificationDoesNotReleaseBeforeQueryCompletion`, `CancelledForegroundWorkDoesNotUnchargeRetiredBytes`, `HiddenViewportKeepsPendingResourcesOwned`, `OldDeviceCompletionCannotReleaseNewDeviceResources`, and `TeardownReleasesEachOwnerExactlyOnce` using manually completed tickets.
- [ ] Run red, then implement the queue and real renderer bridge. First trace the installed Helix host's command submission and viewport host creation. Metadata confirms `DefaultRenderHost.OnRender(TimeSpan)` is protected virtual, its constructor accepts an injected renderer factory, and the host exposes `Renderer.ImmediateContext`; use an application-owned host override that calls the base render before submitting/polling query tickets on that same owner. Record the exact supported viewport host-wiring API and ordering in the ledger before writing the integration. If the installed WPF host cannot be replaced through a supported seam, report that specific integration blocker rather than silently using a deferred context or `OnRendered` completion. The browser plan remains independently executable.
- [ ] Poll query completion asynchronously on the owning rendering path, request rendering when active, and retain uncompleted ownership when hidden. Device loss reports failure or safe teardown, never fabricated success. Test completion/error/timeout/disposal paths green before any scene run. Commit the reviewed retirement bridge.

### Task 4: Integrate part binding ownership and coalesced demand

**Files:** Create `src/Deviant.Desktop/AssetViewport.Residency.cs`, `tests/Deviant.Desktop.Tests/ViewportMaterialResidencyTests.cs`; modify `AssetViewport.xaml.cs`, `AssetViewport.Streaming.cs`, `ViewportResources.cs` and `SurfaceViewportMaterial.cs` in Desktop.

**Interfaces:**

- Extend `Part` with its descriptor, source material identity and explicit active binding owner. Preserve occurrence/primitive/geometry identities and selection keys.
- Add dispatcher-owned `RequestMaterialResidencyRefresh()` and `Task RefreshMaterialResidencyAsync(CancellationToken token)` to `AssetViewport`; exactly one running refresh and one dirty replacement are permitted.
- Add `AssetViewportService.DescribeResidentTextureKeys()` returning `IReadOnlySet<TextureContentKey>` and `GpuRetirementTicket`-backed binding release through Task 3. Do not expose mutable pool entries.

- [ ] Add `FinalSnapshotReplacesRatherThanAccumulatesMaterialLeases`, `CameraReselectionRejectsStaleUploadCommit`, `TechniqueReplacementPreservesGeometryIdsAndSelection`, `MaterialFailurePreservesOtherParts`, and `UnloadDuringAdmissionDrainsOwners`. Assert no material lifetime-list accumulation, original occurrence IDs, correct layout, error isolation and zero owned resources after teardown.
- [ ] Run red with a simulated renderer/admission seam; integrate Tasks 1–3 across initial load, append and completion. Frame the camera before the first demand calculation. Mark demand dirty on actual rendered camera matrices, selection, source/manual visibility and streamed descriptor updates. Use conservative world-space bounds with `ViewMatrix * ProjectionMatrix`; verify boundary cases and no transpose.
- [ ] Detach evicted bindings, await retirement/accounted release, then admit replacements. Keep placeholder geometry or a bounds/outline selection representation when source render state cannot safely draw a placeholder. Never present a placeholder as final material completion. Stop new uploads when hidden. Cancel preparation on navigation; route already submitted retirements through the queue.
- [ ] Make binding commit atomic across material technique and required UV/tangent geometry; include replacement overlap in admission. Preserve shared texture leases, source snapshot pins, encoded paths and exact-backed pixel lifetime. Return a short `Texture budget reached` state for unresolved pressure and record complete errors privately. Deduplicate retries by descriptor/device/demand generation.
- [ ] Run planner, retirement, material residency, geometry, material-state/depth, streaming and upload regressions. Commit the reviewed production integration.

### Task 5: Prove the exact package under navigation and memory pressure

**Files:** Existing single private scene harness updated in place; new uniquely named private package, cache, output and trace receipts. No copied harness source or edits to frozen evidence.

**Interfaces:** Consume integrated viewport residency; produce exact-package records for coverage, final visible bindings, live/retired/upload bytes, unique resources, frame/dispatcher pacing, cancellation and cleanup.

- [ ] Build the solution and run complete desktop, texture, scene and model test projects. Independently review the integrated diff; fix substantive findings with generated regressions before freezing the package.
- [ ] Run the retained all-region workload against a new private cache. The harness must retain failure metrics and process cleanup; it must not accept a single initial object as scene usability. Inspect opaque, cutout, transparent and character materials together, then traverse disjoint camera views, select meshes, toggle visibility, change scenes, return, minimize/restore, cancel, recreate the device and close.
- [ ] Keep the exact package fixed for three cold and three prepared acceptance runs. Record cold usability, full cold completion, prepared final visible material completion and export separately. Require complete scene/dependency coverage; verify every scripted visible part reaches the requested final material. Report all unsupported content and budget-limited parts as incomplete.
- [ ] Require ten-second scene gates, 250 ms cancellation acknowledgement and two-second foreground drain; retain renderer retirement/teardown evidence. Check flight travel at simulated 30/60/144 Hz and actual frame pacing while loading. A stepping camera fails acceptance despite correct distance tests.
- [ ] Compare final geometry, rigs, UVs, source texture pixels, alpha/color space, LOD overrides and independent rigged FBX readback using existing original and additional-character regressions. Measure prepared export against two minutes. Verify exports retain offscreen content according to the requested selection/scope.
- [ ] Deliver the exact private package location/hashes and reproducible results. Keep limitations and unmet gates explicit; preserve the full remaining asset-pipeline goal. Do not promote the normal executable.

## Self-review and handoff

Tasks cover material descriptions, unique demand, deterministic priority, real retirement, per-part ownership, hidden/device/cancellation behavior, material-state compatibility and complete package acceptance. The host override is supported by inspected metadata; its WPF host-wiring seam still requires source/API inspection before integration. The ticket contract and explicit stopping condition prevent an unsafe fallback.

Plan status: confirmed by the user; execution follows browser acceptance. Execution remains lead-owned and inline; no recurring agents or new execution-method choice.
