using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Models;

namespace Deviant.Desktop;

public partial class CharactersView
{
    private readonly HashSet<Task> rigTasks = [];
    private IReadOnlyList<RigNodeRow> rigRows = [];
    private NativeSkeletonBindingSession.NativeSkeletonBindingLease? rigLease;
    private NativeSkeletonBindingSessionResult? rigResult;
    private CancellationTokenSource? rigCancellation;
    private Func<AssetDescriptor, CharacterModelBindingResult, CancellationToken,
        Task<NativeSkeletonBindingSession.NativeSkeletonBindingLease>>? rigProvider;
    private long rigGeneration;
    private bool clearingRig;

    /// <summary>Loads a retained skeleton binding for the exact selected descriptor and model group.</summary>
    public Func<AssetDescriptor, CharacterModelBindingResult, CancellationToken,
        Task<NativeSkeletonBindingSession.NativeSkeletonBindingLease>>? RigProvider
    {
        get => rigProvider;
        set
        {
            if (ReferenceEquals(rigProvider, value)) return;
            rigProvider = value;
            ClearRig();
            if (value is not null && RigExpander?.IsExpanded == true) StartRigLoad();
        }
    }

    /// <summary>Raised after the view releases its rig lease so the host can release its container owner.</summary>
    public event EventHandler? RigCleared;

    public void ClearRig()
    {
        if (clearingRig) return;
        clearingRig = true;
        try
        {
            ++rigGeneration;
            rigCancellation?.Cancel();
            rigLease?.Dispose();
            rigLease = null;
            rigResult = null;
            rigRows = [];
            if (RigNodes is not null) RigNodes.ItemsSource = null;
            if (RigNameCandidates is not null) RigNameCandidates.ItemsSource = null;
            if (RigSearch is not null) RigSearch.Clear();
            if (RigAdvanced is not null) RigAdvanced.Clear();
            if (RigStatus is not null) RigStatus.Text = "Open to inspect the selected model.";
            if (RigExpander?.IsExpanded == true) RigExpander.IsExpanded = false;
        }
        finally { clearingRig = false; }
        RigCleared?.Invoke(this, EventArgs.Empty);
    }

    public async Task DrainRigAsync()
    {
        ClearRig();
        while (rigTasks.Count > 0)
        {
            var pending = rigTasks.ToArray();
            await Task.WhenAll(pending);
            foreach (var task in pending) rigTasks.Remove(task);
        }
    }

    private void RigExpanded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RigExpander)) return;
        if (!clearingRig) StartRigLoad();
    }

    private void RigCollapsed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RigExpander)) return;
        if (!clearingRig) ClearRig();
    }

    private void StartRigLoad()
    {
        if (rigLease is not null || rigCancellation is not null) return;
        var task = LoadRigAsync();
        rigTasks.Add(task);
        _ = ObserveRigTaskAsync(task);
    }

    private async Task ObserveRigTaskAsync(Task task)
    {
        try { await task; }
        finally { rigTasks.Remove(task); }
    }

    private async Task LoadRigAsync()
    {
        if (RigStatus is null) return;
        if (SelectedDescriptor is not { } descriptor || modelBinding is not { } binding ||
            binding.SelectedIdentity != descriptor.Identity || binding.Revision != descriptor.Revision)
        {
            RigStatus.Text = "Choose a model detail first.";
            return;
        }
        if (RigProvider is not { } provider)
        {
            RigStatus.Text = "Rig details are unavailable.";
            return;
        }

        var version = ++rigGeneration;
        var request = new CancellationTokenSource();
        rigCancellation = request;
        RigStatus.Text = "Reading rig details…";
        try
        {
            var candidate = await provider(descriptor, binding, request.Token);
            if (version != rigGeneration || request.IsCancellationRequested ||
                !ReferenceEquals(descriptor, SelectedDescriptor) || !ReferenceEquals(binding, modelBinding) ||
                !ReferenceEquals(provider, rigProvider))
            {
                candidate.Dispose();
                return;
            }

            try
            {
                candidate.EnsureUnchanged();
                if (candidate.Result.SelectedIdentity != descriptor.Identity ||
                    candidate.Result.ContainerIdentity != binding.ContainerIdentity ||
                    candidate.Result.Revision != descriptor.Revision ||
                    candidate.Result.GroupIndex != binding.GroupIndex)
                    throw new ArchiveException("NativeSkeletonBindingSelectionMismatch", "Rig details belong to another model selection.");
                if (candidate.Binding is null)
                {
                    var result = candidate.Result;
                    rigResult = result;
                    candidate.Dispose();
                    candidate = null!;
                    RigCleared?.Invoke(this, EventArgs.Empty);
                    ShowRigResult(result, null);
                    return;
                }
                var acceptedLease = candidate;
                rigLease = acceptedLease;
                candidate = null!;
                ShowRigLease(acceptedLease);
            }
            finally { candidate?.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            if (version == rigGeneration) RigStatus.Text = "Rig details cancelled.";
        }
        catch (Exception error)
        {
            if (version == rigGeneration)
            {
                ClearRig();
                RigStatus.Text = error switch
                {
                    ArchiveException { Code: "AssetMemoryLimit" } => "Rig details exceed the memory limit.",
                    ArchiveException { Code: "NativeSkeletonBindingNodeArrayMissing" } => "The referenced hierarchy is missing.",
                    ArchiveException { Code: "NativeSkeletonBindingNodeArrayAmbiguous" } => "The referenced hierarchy is ambiguous.",
                    ArchiveException { Code: "NativeSkeletonBindingSelectionMismatch" or "NativeSkeletonBindingReferenceChanged" } => "The selected model changed. Reopen Rig to reload.",
                    _ => "Rig details are unavailable."
                };
                RigAdvanced.Text = error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(rigCancellation, request)) rigCancellation = null;
            var retry = request.IsCancellationRequested && RigExpander?.IsExpanded == true && rigLease is null && rigProvider is not null;
            request.Dispose();
            if (retry) StartRigLoad();
        }
    }

    private void ShowRigLease(NativeSkeletonBindingSession.NativeSkeletonBindingLease lease)
    {
        rigResult = lease.Result;
        ShowRigResult(rigResult, lease.Binding);
    }

    private void ShowRigResult(NativeSkeletonBindingSessionResult result, NativeSkeletonNodeArrayBindingDocument? projection)
    {
        RigStatus.Text = (result.Status switch
        {
            NativeSkeletonBindingSessionStatus.Bound => "Hierarchy and names loaded.",
            NativeSkeletonBindingSessionStatus.EntityNodeMissing => "The hierarchy reference is missing.",
            NativeSkeletonBindingSessionStatus.EntityNodeAmbiguous => "The hierarchy reference has multiple candidates.",
            _ => "The hierarchy reference is unresolved."
        }) + (projection is null ? "" : $" {projection.Entries.Count:N0} nodes.");

        var projectedRows = projection?.Entries.Select(entry => new RigNodeRow(entry)).ToArray() ?? [];
        var byOrdinal = projectedRows.ToDictionary(row => row.Entry.Ordinal);
        foreach (var row in projectedRows)
        {
            var parentValue = row.Entry.Node.StoredParent;
            if (parentValue == uint.MaxValue)
            {
                row.SetParentContext(null, "Root", 0);
                continue;
            }
            if (parentValue >= (uint)projectedRows.Length || !byOrdinal.TryGetValue((int)parentValue, out var parent))
            {
                row.SetParentContext(null, $"Invalid parent {parentValue}", 0);
                continue;
            }

            var depth = 1;
            var seen = new HashSet<int> { row.Entry.Ordinal };
            var cursor = parent;
            while (depth < 9 && seen.Add(cursor.Entry.Ordinal))
            {
                var next = cursor.Entry.Node.StoredParent;
                if (next == uint.MaxValue || next >= (uint)projectedRows.Length || !byOrdinal.TryGetValue((int)next, out var nextParent)) break;
                cursor = nextParent;
                depth++;
            }
            row.SetParentContext(parent, null, depth);
        }
        rigRows = projectedRows;
        RigAdvanced.Text = $"Skeleton: {result.SelectedIdentity.Key}" +
            $"\nContainer: {result.ContainerIdentity.Key}" +
            $"\nRevision: {result.Revision.IndexContentId} · {result.Revision.PhysicalSourceStamp}" +
            $"\nGroup index: {result.GroupIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable"}" +
            $"\nEntity reference: {result.EntityNode}" +
            $"\nReference status: {result.EntityNodeResolution?.Status.ToString() ?? "Unavailable"}" +
            $"\nNode-array identity: {result.NodeArrayIdentity?.Key ?? "Unavailable"}" +
            $"\nDiagnostic: {result.Diagnostic ?? "None"}";
        if (projection is not null)
        {
            RigAdvanced.AppendText($"\nMatrix count: {projection.Entries.Count:N0}" +
                $"\nSkeleton extent: {projection.SkeletonExtent}" +
                $"\nNode-array extent: {projection.NodeArrayExtent}" +
                $"\nEntity extent: {projection.EntityNodeExtent}" +
                $"\nName-table extent: {projection.PrimaryNameTableExtent}");
        }
        if (rigLease?.NodeArray is { StructuralDiagnostics.Count: > 0 } nodeArray)
            RigAdvanced.AppendText($"\nStructure diagnostics: {string.Join(", ", nodeArray.StructuralDiagnostics.Select(item => item.Code).Distinct(StringComparer.Ordinal))}");
        RefreshRigRows();
    }

    private void RigSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshRigRows();

    private void RefreshRigRows()
    {
        if (RigNodes is null) return;
        if (rigLease is not null)
        {
            try { rigLease.EnsureUnchanged(); }
            catch
            {
                ClearRig();
                RigStatus.Text = "The source changed. Open Rig again to reload.";
                return;
            }
        }
        var priorOrdinal = (RigNodes.SelectedItem as RigNodeRow)?.Entry.Ordinal;
        var query = RigSearch.Text.Trim();
        var visible = query.Length == 0 ? rigRows : rigRows.Where(row => row.Matches(query)).ToArray();
        RigNodes.ItemsSource = visible;
        RigNodes.SelectedItem = visible.FirstOrDefault(row => row.Entry.Ordinal == priorOrdinal) ?? visible.FirstOrDefault();
    }

    private void RigNodeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (rigLease is not null)
        {
            try { rigLease.EnsureUnchanged(); }
            catch
            {
                ClearRig();
                RigStatus.Text = "The source changed. Open Rig again to reload.";
                return;
            }
        }
        if (RigNodes.SelectedItem is not RigNodeRow row)
        {
            RigNameCandidates.ItemsSource = null;
            return;
        }
        RigNameCandidates.ItemsSource = row.CandidateRows;
        RigAdvanced.Text = $"Skeleton: {rigResult?.SelectedIdentity.Key ?? "Unavailable"}" +
            $"\nContainer: {rigResult?.ContainerIdentity.Key ?? "Unavailable"}" +
            $"\nRevision: {rigResult?.Revision.IndexContentId ?? "Unavailable"} · {rigResult?.Revision.PhysicalSourceStamp ?? "Unavailable"}" +
            $"\nNode-array identity: {rigResult?.NodeArrayIdentity?.Key ?? "Unavailable"}" +
            $"\nMatrix ordinal: {row.Entry.Ordinal}" +
            $"\nDescriptor key: 0x{row.Entry.Descriptor.Key:X8}" +
            $"\nName-match status: {row.Entry.NameMatch.Status}" +
            $"\nParent: {row.ParentLabel} (stored value {row.Entry.Node.StoredParent})" +
            $"\nStored child ordinals: {DescribeChildOrdinals(row.Entry.Node.Children)}" +
            $"\nSession status: {rigResult?.Status.ToString() ?? "Unavailable"}" +
            $"\nNode flags: 0x{row.Entry.Node.Flags:X8}" +
            $"\nDescriptor words: 0x{row.Entry.Descriptor.Word16A:X4}, 0x{row.Entry.Descriptor.Word16B:X4}, 0x{row.Entry.Descriptor.RawWord:X8}" +
            $"\nMatrix extent: {row.Entry.StoredMatrix.Extent}" +
            $"\nNode extent: {row.Entry.Node.Extent}" +
            $"\nDescriptor extent: {row.Entry.Descriptor.Extent}";
        var selectedCandidate = row.CandidateRows.FirstOrDefault();
        if (selectedCandidate is not null)
            RigAdvanced.AppendText($"\nFirst name entry: {selectedCandidate.Entry.Ordinal}, key 0x{selectedCandidate.Entry.Key:X8}, bytes {selectedCandidate.Entry.ByteCount}, extent {selectedCandidate.Entry.Extent}");
    }

    private static string DescribeChildOrdinals(IReadOnlyList<uint> children)
    {
        const int maximumShown = 64;
        var shown = string.Join(", ", children.Take(maximumShown));
        return children.Count > maximumShown ? $"{shown}, … ({children.Count:N0} total)" : shown;
    }

    private sealed class RigNodeRow
    {
        public RigNodeRow(NativeSkeletonNodeArrayBindingEntry entry)
        {
            Entry = entry;
            CandidateRows = new RigNameCandidateRows(entry.NameMatch.Entries);
        }

        public NativeSkeletonNodeArrayBindingEntry Entry { get; }
        public RigNameCandidateRows CandidateRows { get; }
        public string NameLabel => Entry.NameMatch.Status switch
        {
            NativeSkeletonNodeNameMatchStatus.Missing => $"Unnamed node {Entry.Ordinal + 1:N0}",
            NativeSkeletonNodeNameMatchStatus.Unique => CandidateRows.Count == 1
                ? CandidateRows[0].Name : $"Unnamed node {Entry.Ordinal + 1:N0}",
            _ => $"{CandidateRows.Count:N0} name candidates"
        };
        public string ParentLabel => ParentRow?.NameLabel ?? ParentLabelValue;
        private RigNodeRow? ParentRow { get; set; }
        private string ParentLabelValue { get; set; } = "Unavailable";
        public int Depth { get; private set; }
        public string Label => $"Node {Entry.Ordinal + 1:N0} · {NameLabel} · Parent {ParentLabel} · {Entry.Node.Children.Count:N0} children";
        public System.Windows.Thickness IndentMargin => new(Math.Min(Depth, 8) * 12, 0, 0, 0);
        public bool Matches(string query)
        {
            if (Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                ParentLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
            foreach (var candidate in CandidateRows)
                if (candidate.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
            return false;
        }

        public void SetParentContext(RigNodeRow? parent, string? parentLabel, int depth)
        {
            ParentRow = parent;
            ParentLabelValue = parentLabel ?? "Unavailable";
            Depth = Math.Min(depth, 8);
        }
    }

    private sealed class RigNameCandidateRows(IReadOnlyList<SkeletonNameEntry> entries) : IList<RigNameCandidateRow>, System.Collections.IList
    {
        public int Count => entries.Count;
        public RigNameCandidateRow this[int index] => new(entries[index]);
        RigNameCandidateRow IList<RigNameCandidateRow>.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
        object? System.Collections.IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
        public bool IsReadOnly => true;
        bool System.Collections.IList.IsReadOnly => true;
        bool System.Collections.IList.IsFixedSize => true;
        bool System.Collections.ICollection.IsSynchronized => false;
        object System.Collections.ICollection.SyncRoot => this;
        public void Add(RigNameCandidateRow item) => throw new NotSupportedException();
        int System.Collections.IList.Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(RigNameCandidateRow item) => false;
        bool System.Collections.IList.Contains(object? value) => value is RigNameCandidateRow row && Contains(row);
        public void CopyTo(RigNameCandidateRow[] array, int arrayIndex)
        { for (var index = 0; index < entries.Count; index++) array[arrayIndex + index] = this[index]; }
        void System.Collections.ICollection.CopyTo(Array array, int index)
        { for (var itemIndex = 0; itemIndex < entries.Count; itemIndex++) array.SetValue(this[itemIndex], index + itemIndex); }
        public IEnumerator<RigNameCandidateRow> GetEnumerator()
        {
            for (var index = 0; index < entries.Count; index++) yield return new(entries[index]);
        }
        public int IndexOf(RigNameCandidateRow item) => -1;
        int System.Collections.IList.IndexOf(object? value) => -1;
        public void Insert(int index, RigNameCandidateRow item) => throw new NotSupportedException();
        void System.Collections.IList.Insert(int index, object? value) => throw new NotSupportedException();
        public bool Remove(RigNameCandidateRow item) => throw new NotSupportedException();
        void System.Collections.IList.Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RigNameCandidateRow
    {
        public RigNameCandidateRow(SkeletonNameEntry entry)
        {
            Entry = entry;
            Name = entry.TryGetUtf8Text(out var name) && !string.IsNullOrEmpty(name) ? name : "Unreadable stored name";
        }
        public SkeletonNameEntry Entry { get; }
        public string Name { get; }
        public string Label => $"Entry {Entry.Ordinal + 1:N0} · {Name}";
    }
}
