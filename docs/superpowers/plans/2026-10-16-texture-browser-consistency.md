# Texture Browser Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep texture pages, filter options, saved selections and closing consistent while indexing advances.

**Architecture:** Publish queryable projection generations in writer transactions. A logical read lease pins one generation across short SQLite read transactions; one response supplies page, count, coverage and options. Typed desktop query state owns the requested filters independently of WPF option lists.

**Tech Stack:** C#, .NET 10, WPF, Microsoft.Data.Sqlite, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-16-browser-consistency-and-material-residency-design.md`, sections 1 and 3. GPU residency has its own subsequent plan.

## Global Constraints

- Existing main checkout only; preserve other changes. No new worktrees or parallel source copies.
- Lead owns integration, compiler, packaged UI, GPU and desktop input; retain the existing inline execution method.
- Public tests use generated fixtures. Private samples, machine paths and workload identities stay in ignored diagnostics.
- Cached pages/search and cancellation acknowledgement: at most 250 ms. Outstanding foreground work drains within two seconds. Prepared supported texture preview: at most two seconds.
- Preserve library data, existing CPU/GPU limits, the 20 GB disk-cache setting, normal executable and frozen packages.
- Read leases retain logical history, not a SQLite transaction between user actions. No bulk decoding or evidence restoration to open pages.
- No new push, release or normal-build promotion is part of execution.

## Review Focus

1. An alias edit from another store changes sorting while a page is pinned: the old page sequence remains stable (Task 1).
2. A requested catalog resolves before the global catalog list catches up: its filter never becomes unrestricted (Tasks 2–3).
3. A stale request finishes after navigation or workspace replacement: it publishes no state (Tasks 2–3).
4. A refresh faults before closing begins: all sessions, read leases and connections still close (Task 4).
5. A legacy cache needs derived indexes, or one row is corrupt: bounded writer work repairs it without blocking the dispatcher or erasing aliases/bookmarks (Tasks 1–2, 5).

## File structure and execution

Keep contracts in Assets, database publication/read ownership in Catalog, archive/direct-catalog resolution in Browser, and presentation state in Desktop. Add focused partial files instead of extending the large existing session/view files with unrelated responsibilities. Each task uses red/green tests, targeted verification and a scoped local commit after review; never stage unrelated changes.

Use these PowerShell variables for the commands below:

```powershell
$textureTests = 'tests/Deviant.Texture.Tests/Deviant.Texture.Tests.csproj'
$desktopTests = 'tests/Deviant.Desktop.Tests/Deviant.Desktop.Tests.csproj'
```

Every build/test invocation includes `-c Release -p:GenerateDesktopArtifacts=false -p:UseSharedCompilation=false`. Retain private logs. Desktop test runs additionally use `--blame-hang-timeout 30s`; an aborted host is a failure, not a pass.

### Task 1: Publish complete generations and read them without repair writes

**Files:** Create `src/Deviant.Assets/TextureBrowseContracts.cs`, `src/Deviant.Catalog/FeatureStore.TextureBrowse.cs`, `tests/Deviant.Texture.Tests/TextureBrowseGenerationTests.cs`, `tests/Deviant.Texture.Tests/GeneratedTextureProjectionFixture.cs`. Modify `FeatureStore.TextureProjections.cs`, `FeatureStore.TextureReads.cs`, `FeatureStore.cs` in Catalog, and existing projection tests.

**Interfaces:**

- Add `TextureBrowseToken(SourceRevision Revision, string Profile, long Generation)` and `TextureBrowseResponse(TextureBrowseToken Token, TextureDiscoveryPage Page, TextureDiscoveryFilterOptions Options, TextureNamingCoverage Coverage)` in Assets.
- Add `FeatureStore.AcquireTextureBrowseReadLease(string profile, SourceRevision revision, long? generation = null)` returning nested `TextureBrowseReadLease : IDisposable`.
- The lease exposes `TextureBrowseToken Token` and `Task<TextureBrowseResponse> ReadAsync(TextureDiscoveryQuery query, CancellationToken token = default)`; concurrent requests use separate short connections. Disposing cancels and drains active reads.
- Existing page/options APIs remain adapters. A response does not combine separate generation captures.

- [ ] Add deterministic generated tests `PublicationDuringReadKeepsOptionsRowsAndCountsTogether`, `AliasEditFromAnotherStorePreservesPinnedPaging`, `CancelledPublicationLeavesOldHeadQueryable`, `LegacyPreparationPublishesOnlyCompleteBatches`, and `DisposalCancelsAndReleasesIndependentReaders`. Reuse a factored generated fixture and diagnostic barriers rather than timing sleeps. Assert respectively: all response fields use the pinned token; alias/order/search remain old until a new pin; rollback changes neither head nor checkpoint; incomplete migrations never become the published head; readers drain within two seconds with zero live connections.
- [ ] Run `dotnet test $textureTests` with the common flags and `--filter FullyQualifiedName~TextureBrowseGenerationTests`; observe a relevant failure before implementing.
- [ ] Implement versioned alias values, including deletions, in the same generation transaction as their scalar search/sort projections. Convert alias replacements, direct-catalog commits, owner commits and quarantine repair to prepare affected scalar/history/role data before advancing the published head. Preserve original library aliases and existing evidence. Add versioned, transactional migration markers; bounded batches yield between writer transactions. Never use increased foreground retry counts as the publication protocol.
- [ ] Implement the read lease and independent connection queries, including catalog labels/membership and generation-bound coverage. Keep historical rows for active pins; existing history refresh must not rewrite old generations with current aliases. Reset/replacement invalidates old sessions explicitly. Leave history pruning disabled in this task rather than adding an untested collector.
- [ ] Run new generation tests plus `TextureProjectionStoreTests`, `TextureProjectionOptionsTests` and `TextureProjectionQuarantineTests`. Update tests that expected aliases to change without a new generation: a new pin sees the edit, an old pin remains stable. Confirm new foreground reads issue no schema/backfill writes, and check scalar query plans retain bounded indexed access. Commit only this task's reviewed files.

### Task 2: Bind browser requests and direct catalogs to one generation

**Files:** Create `src/Deviant.Browser/TextureDiscoverySession.Browsing.cs`; modify `src/Deviant.Browser/TextureDiscoverySession.cs`, `src/Deviant.Assets/TextureBrowseContracts.cs`; extend `tests/Deviant.Texture.Tests/TextureDiscoverySessionTests.cs`.

**Interfaces:**

- Add `ITextureBrowseSession : IAsyncDisposable` with `Task<TextureBrowseResponse> ReadAsync(TextureDiscoveryQuery query, CancellationToken token = default)`.
- Add `TextureDiscoverySession.OpenBrowseAsync(TextureDiscoveryQuery query, CancellationToken token = default)` returning `Task<ITextureBrowseSession>`; its request resolves the requested catalog before pinning and retains that pin for later offsets of the same query. Changing non-page filters opens a new session.
- Keep `ITextureDiscoveryCatalog` compatible. The production desktop will use the new session; a generated/nonproduction catalog adapter captures one immutable in-memory snapshot, rather than pretending separate mutable reads are consistent.

- [ ] Add `DirectCatalogCanPublishBeforeGlobalDiscoveryEnds`, `LaterOwnerDiscoveryDoesNotChangePinnedPage`, `SourceReplacementInvalidatesBrowseSession`, and `CorruptEntryRepairDoesNotPublishMixedGenerations`. Assert exact requested catalog membership while unrelated discovery remains blocked, stable token/count across subsequent offsets, source-change rejection, and repair publication under a new token without exposing invalid evidence.
- [ ] Run these tests red with `dotnet test $textureTests` and a filter matching their names.
- [ ] Move browse-session ownership and response assembly into the new partial file. Resolve and revalidate direct catalog membership through the existing catalog reader, commit its derived projection, then acquire Task 1's lease. Do not append `catalogs.Groups` from mutable session state to a pinned response. Isolate invalid rows through bounded writer repair; retry the affected request against a newly published generation without changing its query.
- [ ] Run the new tests and all `TextureDiscoverySessionTests`; verify cancellation/disposal release archive/read owners. Commit the reviewed browser integration.

### Task 3: Preserve query intent across asynchronous option refreshes

**Files:** Create `src/Deviant.Desktop/TextureBrowserQueryState.cs`, `src/Deviant.Desktop/TextureExplorerView.Browsing.cs`, `tests/Deviant.Desktop.Tests/TextureBrowserQueryStateTests.cs`; modify `TextureExplorerView.xaml.cs`, `AudioWindow.TextureCatalogs.cs`, `AudioWindow.Navigation.cs` in Desktop and `TextureCatalogWorkflowTests.cs` in Desktop.Tests.

**Interfaces:**

- Internal immutable `TextureBrowserQueryState(TextureDiscoveryQuery Query, ResourceIdentity? SelectedIdentity)` provides `static FromSaved(IReadOnlyDictionary<string,string> values)` and `IReadOnlyDictionary<string,string> ToSaved()` using the existing saved-filter keys. Parse errors retain actionable unavailable-state diagnostics; no key silently becomes unrestricted.
- `TextureExplorerView` owns that state, a browse session, a monotonically increasing query version, and one running/one pending refresh. Existing public `ApplyFilterAsync`, `CaptureFilter` and selection methods keep their signatures.
- One publication function `PublishBrowseResponse(TextureBrowseResponse response, long requestVersion)` updates rows, counts, options and selection together only if workspace, navigation, query and lifetime still match.

- [ ] Add generated tests `SavedCatalogSurvivesAnOlderOptionsResponse`, `MissingCatalogRemainsFilteredAndUnavailable`, `ExplicitAnySelectionClearsTheSavedKey`, `ObsoleteRefreshCannotOverwriteNewSelection`, and `PagingUsesTheSameBrowseToken`. Assert preserved `group`, no unrestricted query on missing/pending resolution, an explicit user reset clearing it, newer selection winning, and one response token across pages.
- [ ] Run those tests red, then reproduce `SavedCatalogKeyReadsRequestedGroupBeforeGlobalDiscoveryCompletes` unchanged. Retain the complete failure; do not rewrite the original assertion to accept an empty key.
- [ ] Implement typed query ownership and Task 2's browse session. Control handlers explicitly change query state; option replacement only reflects state and retains pending/unavailable choices. Apply saved keys before awaiting options, resolve a direct catalog without global enrichment, preserve identities during result replacement, and cancel stale work on navigation/reselection. Move production explorer context refresh onto the same published response so it cannot reintroduce a separate generation.
- [ ] Run new tests, the original saved-catalog test and `TextureCatalogWorkflowTests` with the desktop timeout. Commit the reviewed view/navigation changes.

### Task 4: Finish cleanup despite refresh failures

**Files:** Create `src/Deviant.Desktop/AudioWindow.TextureLifetime.cs`; modify `AudioWindow.TextureCatalogs.cs`, `AudioWindow.xaml.cs` in Desktop; extend `tests/Deviant.Desktop.Tests/TextureCatalogWorkflowTests.cs` and `WorkspaceCommitTests.cs`.

**Interfaces:**

- Preserve `ClearTextureCatalogsAsync()` and `Window_Closing` entry points. Add `Task DrainTextureBrowseWorkAsync()` to cancel, observe and clear outstanding refresh/navigation work before disposing owners. Keep all full failures in the existing diagnostic path, with a local collected error list so one error cannot skip remaining disposals.

- [ ] Add `CloseAfterRefreshFaultDisposesEveryOwner` and `NavigationAfterRefreshFaultCanReopenTextures`. Inject a deterministic failing refresh and retained read owner through the existing test seams/reflection style; assert actual `Closed`, no dispatcher-unhandled exception, all disposal callbacks run, and navigation/reopening succeeds. Assert cancellation acknowledgement within 250 ms and drain within two seconds under controlled fixture scheduling.
- [ ] Observe red failures and rerun `AudioUpdatesCannotOverwriteTheVisibleBoundedCatalogState` without changing its user-visible expectations.
- [ ] Implement cancellation-before-disposal and exception-containing close/drain paths. Separate cancellation, ordinary generation replacement, source change and real read failure. Drain all operations and attempt all owner disposals even if an earlier task or disposal throws. Closing records an aggregate cleanup failure rather than throwing out of `async void` or reporting success early.
- [ ] Run the new tests, both original failures, `WorkspaceCommitTests` and `RecordsNavigationTests`. Require host exit and complete fixture cleanup. Commit the reviewed lifetime changes.

### Task 5: Verify browser behavior in one exact private package

**Files:** Existing private package/harness tooling and new ignored result/trace names; no new benchmark source tree. Modify public regression tests only if a reproduced defect warrants them.

**Interfaces:** Consume Tasks 1–4's generation-bound production path and existing UI controls. Produce a private result manifest containing source/package hashes, workload/cache identities, actions, timings, failure codes and process/owner cleanup evidence.

- [ ] Run the full texture and desktop test projects using the common flags. Run `dotnet build Deviant.slnx` with those flags. Require no failed or aborted run; record existing skips separately.
- [ ] Freeze a new private package without invoking the normal-build promotion script. Exercise existing and fresh private caches, interrupted indexing, one corrupt entry and source replacement. Use the actual UI for opening, saved catalog filters, search, paging, rapid selection, Home, workspace switch, cancellation, close and reopen. Keep retail identifiers and machine paths private.
- [ ] Measure cached page/search completion and cancellation acknowledgement against 250 ms, controlled drain against two seconds, supported prepared preview against two seconds, and dispatcher heartbeat throughout. Record sample counts and every outlier, not just averages. Verify correct rows, names, selection, pixels and absence of stale publications.
- [ ] Review the diff and results with one bounded independent reviewer. Repair concrete findings, rerun affected checks, then record acceptance or the exact remaining defect in the private ledger. Browser acceptance is a prerequisite to the GPU plan, not completion of the full thread goal.

## Self-review and handoff

The tasks cover published generations, aliases, catalog arrival, authoritative queries, stale results, fault-contained cleanup, legacy/corrupt caches and packaged timing. Existing library data and direct-catalog export remain regression requirements. The full asset-pipeline goal remains unchanged.

Plan status: confirmed by the user; implementation in progress. Fault-contained cleanup, snapshot facet reads and authoritative desktop query state are under verification. Unified page/coverage responses, versioned aliases, packaged timing and full acceptance remain open. Execution remains lead-owned and inline in the existing main checkout.
