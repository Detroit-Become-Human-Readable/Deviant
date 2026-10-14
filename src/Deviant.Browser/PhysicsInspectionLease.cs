using System.Globalization;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

/// <summary>Retains a native container while exposing one exact physics frame inspection.</summary>
public sealed class PhysicsInspectionLease : IDisposable
{
    private const long MetadataReservationBytes = 8L * 1024 * 1024;
    private NativeContainerLease? container;
    private IDisposable? borrowedLease;
    private AssetMemoryBudget.Reservation? metadataReservation;
    private PhysicsMetadataInspectionResult? result;
    private readonly SourceRevision revision;
    private readonly AssetMemoryBudget memoryBudget;
    private int disposed;

    private PhysicsInspectionLease(NativeContainerLease container, IDisposable borrowedLease,
        AssetMemoryBudget.Reservation metadataReservation, ResourceIdentity memberIdentity,
        PhysicsMetadataInspectionResult result)
    {
        this.container = container;
        this.borrowedLease = borrowedLease;
        this.metadataReservation = metadataReservation;
        revision = container.Revision;
        memoryBudget = container.MemoryBudget;
        MemberIdentity = memberIdentity;
        this.result = result;
    }

    public ResourceIdentity MemberIdentity { get; }
    public SourceRevision Revision => revision;
    public AssetMemoryBudget MemoryBudget => memoryBudget;

    public PhysicsMetadataInspectionResult Result
    {
        get
        {
            ThrowIfDisposed();
            return result ?? throw new ObjectDisposedException(nameof(PhysicsInspectionLease));
        }
    }

    public static async Task<PhysicsInspectionLease> OpenAsync(ArchiveSnapshot snapshot,
        ResourceIdentity exactMember, AssetMemoryBudget budget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(exactMember);
        ArgumentNullException.ThrowIfNull(budget);
        token.ThrowIfCancellationRequested();
        ValidateMemberOwner(snapshot.IndexContentId, snapshot.Records, exactMember);
        ValidatePhysicsMember(exactMember);

        var containerIdentity = new ResourceIdentity(exactMember.IndexContentId, exactMember.Ordinal);
        using var container = await NativeContainerLease.OpenAsync(snapshot, containerIdentity, budget, token)
            .ConfigureAwait(false);
        return FromContainer(container, exactMember, token);
    }

    /// <summary>Inspects one exact physics frame using the existing native-container decode and directory.</summary>
    public static PhysicsInspectionLease FromContainer(NativeContainerLease container,
        ResourceIdentity exactMember, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(exactMember);
        token.ThrowIfCancellationRequested();

        IDisposable? borrowed = null;
        AssetMemoryBudget.Reservation? reservation = null;
        try
        {
            borrowed = container.AcquireLease();
            ValidateMemberOwner(container.Snapshot.IndexContentId, container.Snapshot.Records, exactMember);
            ValidateContainer(container, exactMember);
            container.EnsureUnchanged();

            reservation = container.MemoryBudget.Reserve(MetadataReservationBytes, "physics metadata inspection");
            token.ThrowIfCancellationRequested();

            var frames = container.Directory.Objects
                .Where(frame => frame.NativeType == PhysicsEnvelopeReader.NativeType && frame.Identity == exactMember)
                .ToArray();
            if (frames.Length != 1)
                throw new ArchiveException("PhysicsMemberUnavailable", "The exact physics member is missing or ambiguous in the selected container.");

            var precedingSteps = ContainerSteps(container);
            var inspection = new PhysicsMetadataInspectionReader().Read(frames[0], container.Revision,
                container.SourceExtents, precedingSteps, token);
            token.ThrowIfCancellationRequested();
            container.EnsureUnchanged();

            var lease = new PhysicsInspectionLease(container, borrowed, reservation, exactMember, inspection);
            borrowed = null;
            reservation = null;
            return lease;
        }
        catch
        {
            reservation?.Dispose();
            borrowed?.Dispose();
            throw;
        }
    }

    /// <summary>Rechecks the source revision retained by the shared native-container lease.</summary>
    public void EnsureUnchanged()
    {
        ThrowIfDisposed();
        var current = container ?? throw new ObjectDisposedException(nameof(PhysicsInspectionLease));
        ValidateContainer(current, MemberIdentity);
        current.EnsureUnchanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref result, null);
        Interlocked.Exchange(ref container, null);
        Interlocked.Exchange(ref metadataReservation, null)?.Dispose();
        Interlocked.Exchange(ref borrowedLease, null)?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static IReadOnlyList<DecodeStep> ContainerSteps(NativeContainerLease container)
    {
        var steps = container.Segments?.Steps.ToList() ?? [];
        if (container.Segments is null)
            steps.Add(new(nameof(NativeObjectDirectoryLoader), NativeObjectDirectoryLoader.DirectVersion, "QZIP direct container"));
        steps.Add(new(nameof(NativeObjectDirectoryReader), NativeObjectDirectoryReader.Version, "borrowed shared directory"));
        return steps;
    }

    private static void ValidateMemberOwner(string indexContentId, IReadOnlyList<ResourceRecord> records,
        ResourceIdentity member)
    {
        if (member.IndexContentId != indexContentId || member.MemberPath is null || member.Ordinal < 0 ||
            member.Ordinal >= records.Count || records[checked((int)member.Ordinal)].Type != 29)
            throw new ArchiveException("PhysicsSourceMismatch", "Select a physics member from a native container in this archive revision.");
    }

    private static void ValidateContainer(NativeContainerLease container, ResourceIdentity member)
    {
        var snapshot = container.Snapshot;
        var expectedRevision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        if (container.Revision != expectedRevision || container.Directory.Revision != expectedRevision ||
            container.Identity.IndexContentId != snapshot.IndexContentId || container.Identity.MemberPath is not null ||
            container.StoredSource.Identity != container.Identity || container.StoredSource.Type != 29 ||
            container.Directory.Identity != container.Identity || member.IndexContentId != snapshot.IndexContentId ||
            member.Ordinal != container.Identity.Ordinal || member.MemberPath is null)
            throw new ArchiveException("PhysicsSourceMismatch", "The native container and physics member do not match the selected source revision.");

        ValidatePhysicsMember(member);
    }

    private static void ValidatePhysicsMember(ResourceIdentity member)
    {
        var path = member.MemberPath;
        if (path is null)
            throw new ArchiveException("PhysicsMemberUnavailable", "The physics member identity is not fully qualified.");
        var parts = path.Split('/');
        if (parts.Length != 4 || parts[0] != "dc-object" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var directoryIndex) || directoryIndex < 0 ||
            parts[1] != directoryIndex.ToString(CultureInfo.InvariantCulture) ||
            parts[2] != PhysicsEnvelopeReader.NativeType.ToString(CultureInfo.InvariantCulture) ||
            !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeId) ||
            parts[3] != nativeId.ToString(CultureInfo.InvariantCulture))
            throw new ArchiveException("PhysicsMemberUnavailable", "The physics member identity is not fully qualified.");
    }
}
