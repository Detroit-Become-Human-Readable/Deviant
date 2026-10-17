using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class GmkInspectionLeaseTests
{
    private const uint ContainerId = 731;
    private const uint NativeId = 41;

    [Fact]
    public async Task SelectsExactDuplicateNameItemAndProjectsItsRawFields()
    {
        using var fixture = CreateFixture((4241, NativeId, GroupPayload()));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();

        var lease = await OpenAsync(snapshot, Member(snapshot), budget, itemIndex: 1);
        try
        {
            Assert.Equal(Member(snapshot), Get(lease, "MemberIdentity"));
            Assert.Equal(new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), Get(lease, "Revision"));
            Assert.Equal(1, Get(lease, "SelectedItemIndex"));

            var document = ResultValue(Get(lease, "Document"));
            var items = (System.Collections.IEnumerable)Required(document, "Items");
            var array = items.Cast<object>().ToArray();
            Assert.Equal(2, array.Length);
            Assert.Equal("Duplicate", Get(array[0], "StoredName", "DisplayText"));
            Assert.Equal("Duplicate", Get(array[1], "StoredName", "DisplayText"));
            Assert.Equal((byte)1, Get(array[1], "ReferenceFlag"));
            var pair = Required(array[1], "ReferencePair");
            Assert.Equal(uint.MaxValue, Get(pair, "RawWord0"));
            Assert.Equal(7u, Get(pair, "RawWord1"));
            Assert.Equal(0u, Get(pair, "NormalizedWord0"));
            Assert.Equal(7u, Get(pair, "NormalizedWord1"));

            var inspection = ResultValue(Get(lease, "Inspection"));
            Assert.Equal("Common group", Get(inspection, "Format"));
            Assert.Equal("GMK_COMG v22", Get(inspection, "Profile"));
            var sections = ((System.Collections.IEnumerable)Required(inspection, "Sections")).Cast<object>().ToArray();
            Assert.Contains(sections, section => Bytes(section).AsSpan().SequenceEqual("Duplicate"u8));
            Assert.Contains(sections, section => Bytes(section).AsSpan().SequenceEqual("second text"u8));
            var pairSection = Assert.Single(sections, section => Bytes(section).AsSpan().SequenceEqual(PairBytes(uint.MaxValue, 7)));
            Assert.Equal(8, Bytes(pairSection).Length);
            Assert.NotNull(Get(pairSection, "DecodedExtent"));
            Assert.True(budget.UsedBytes > 0);
            Invoke(lease, "EnsureUnchanged");
        }
        finally { Dispose(lease); }

        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task DefaultsToFirstItemAndBorrowedLeaseSurvivesOwnerDisposal()
    {
        using var fixture = CreateFixture((4241, NativeId, GroupPayload()));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var row = Assert.Single(snapshot.Records, record => record.Type == 29);
        var budget = new AssetMemoryBudget();
        using var owner = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, row.Ordinal), budget);
        var lease = FromContainer(owner, Member(snapshot));

        owner.Dispose();
        try
        {
            Assert.Equal(0, Get(lease, "SelectedItemIndex"));
            Assert.NotNull(ResultValue(Get(lease, "Document")));
            Invoke(lease, "EnsureUnchanged");
        }
        finally { Dispose(lease); }

        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task RejectsInvalidIdentityAndSelectionWithoutLeakingBudget()
    {
        using var fixture = CreateFixture((4241, NativeId, GroupPayload()), (4241, 42, GroupPayload()));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var row = Assert.Single(snapshot.Records, record => record.Type == 29);
        var budget = new AssetMemoryBudget();
        using var owner = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, row.Ordinal), budget);
        var before = budget.UsedBytes;

        Assert.Throws<ArchiveException>(() => FromContainer(owner, Member(snapshot) with { MemberPath = $"dc-object/00/4241/{NativeId}" }));
        Assert.Throws<ArchiveException>(() => FromContainer(owner, Member(snapshot) with { MemberPath = $"dc-object/0/4241/{NativeId + 1}" }));
        Assert.Throws<ArchiveException>(() => FromContainer(owner, Member(snapshot) with { MemberPath = $"dc-object/0/4242/{NativeId}" }));
        Assert.Throws<ArchiveException>(() => FromContainer(owner, Member(snapshot) with { IndexContentId = "another-revision" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FromContainer(owner, Member(snapshot), itemIndex: 2));

        Assert.Equal(before, budget.UsedBytes);
    }

    [Fact]
    public async Task CancellationAndBudgetPressureReleaseTemporaryLeaseState()
    {
        using var fixture = CreateFixture((4241, NativeId, GroupPayload()));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var row = Assert.Single(snapshot.Records, record => record.Type == 29);
        var budget = new AssetMemoryBudget(256L * 1024 * 1024);
        using var owner = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, row.Ordinal), budget);
        var member = Member(snapshot);
        var ownerBytes = budget.UsedBytes;

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => FromContainer(owner, member, canceled.Token));
        }
        Assert.Equal(ownerBytes, budget.UsedBytes);

        using (var pressure = budget.Reserve(budget.MaximumBytes - budget.UsedBytes - 64L * 1024 * 1024 + 1,
            "generated budget pressure"))
        {
            Assert.Throws<ArchiveException>(() => FromContainer(owner, member));
            Assert.Equal(ownerBytes + pressure.Bytes, budget.UsedBytes);
        }

        owner.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task EnsureUnchangedRejectsChangedBackingVolume()
    {
        using var fixture = CreateFixture((4241, NativeId, GroupPayload()));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var row = Assert.Single(snapshot.Records, record => record.Type == 29);
        var volume = Path.Combine(fixture.DirectoryPath, row.VolumeName);
        var bytes = await File.ReadAllBytesAsync(volume);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(volume, bytes);
        // Fast same-length writes can retain the original filesystem timestamps.
        var expectedStamp = snapshot.Volumes.Single(source => source.FilePath == volume).Stamp;
        File.SetLastWriteTimeUtc(volume, new DateTime(expectedStamp.LastWriteUtcTicks, DateTimeKind.Utc).AddMinutes(1));
        Assert.False(SourceIdentity.SameStamp(expectedStamp, new FileRevisionProvider().Capture(volume)));

        var error = await Assert.ThrowsAsync<ArchiveException>(async () =>
        {
            // Preserve cleanup even if the expected source-change rejection regresses.
            var unexpectedLease = await OpenAsync(snapshot, Member(snapshot), budget);
            Dispose(unexpectedLease);
        });
        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task MalformedGroupRetainsFailureDiagnosticsWithoutInspectionDocument()
    {
        using var fixture = CreateFixture((4241, NativeId, [1, 2, 3, 4]));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await OpenAsync(snapshot, Member(snapshot), budget);
        try
        {
            var document = Get(lease, "Document")!;
            Assert.Null(Get(document, "Value"));
            Assert.NotEmpty(((System.Collections.IEnumerable)Required(document, "Diagnostics")).Cast<object>());
            var inspection = Get(lease, "Inspection")!;
            Assert.Null(Get(inspection, "Value"));
        }
        finally { Dispose(lease); }

        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InspectionUsesParentIdentityWhileLeaseAndDocumentKeepExactMember()
    {
        using var fixture = CreateFixture((4241, NativeId, GroupPayload()));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var member = Member(snapshot);
        var lease = await OpenAsync(snapshot, member, budget, itemIndex: 1);
        try
        {
            var parent = new ResourceIdentity(snapshot.IndexContentId, member.Ordinal);
            Assert.Equal(member, Get(lease, "MemberIdentity"));
            Assert.Equal(member, Get(ResultValue(Get(lease, "Document")), "Identity"));

            var inspectionResult = Get(lease, "Inspection")!;
            Assert.Equal(parent, Get(inspectionResult, "Identity"));
            var inspection = ResultValue(inspectionResult);
            var fields = (IReadOnlyDictionary<string, string>)Required(inspection, "Fields");
            Assert.Equal(member.MemberPath, fields["Source member"]);
            Assert.Equal("1", fields["Item"]);
        }
        finally { Dispose(lease); }

        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InspectionUsesDecodedSectionsAndPreservesSharedDecodeChain()
    {
        using var fixture = CreateFixture((4241, NativeId, GroupPayload()));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await OpenAsync(snapshot, Member(snapshot), budget, itemIndex: 1);
        try
        {
            var result = Get(lease, "Inspection")!;
            var inspection = ResultValue(result);
            var sections = ((System.Collections.IEnumerable)Required(inspection, "Sections")).Cast<object>().ToArray();
            Assert.NotEmpty(sections);
            Assert.All(sections, section => Assert.Equal(BufferSectionKind.DecodedBuffer, Get(section, "Kind")));

            var steps = ((System.Collections.IEnumerable)Required(result, "Steps")).Cast<object>()
                .Select(step => (string)Required(step, "Decoder")).ToArray();
            var loader = Array.IndexOf(steps, "NativeObjectDirectoryLoader");
            var directory = Array.IndexOf(steps, "NativeObjectDirectoryReader");
            var gmk = Array.IndexOf(steps, "GmkCommonGroupReader");
            Assert.True(loader >= 0 && directory > loader && gmk > directory,
                $"Expected shared container steps before G1; found {string.Join(", ", steps)}.");

            var fields = (IReadOnlyDictionary<string, string>)Required(inspection, "Fields");
            Assert.Equal("Duplicate", fields["Name"]);
            Assert.Equal("second text", fields["Stored text"]);
            Assert.Equal("1", fields["Word count"]);
        }
        finally { Dispose(lease); }

        Assert.Equal(0, budget.UsedBytes);
    }

    private static async Task<object> OpenAsync(ArchiveSnapshot snapshot, ResourceIdentity member,
        AssetMemoryBudget budget, CancellationToken token = default, int? itemIndex = null)
    {
        var api = LeaseType();
        var method = api.GetMethod("OpenAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException("GmkInspectionLease.OpenAsync is missing.");
        var task = (Task)(InvokeMethod(method, null, [snapshot, member, budget, token, itemIndex])
            ?? throw new Xunit.Sdk.XunitException("OpenAsync returned null."));
        await task.ConfigureAwait(false);
        return Required(task, "Result");
    }

    private static object FromContainer(NativeContainerLease owner, ResourceIdentity member,
        CancellationToken token = default, int? itemIndex = null)
    {
        var method = LeaseType().GetMethod("FromContainer", BindingFlags.Public | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException("GmkInspectionLease.FromContainer is missing.");
        return InvokeMethod(method, null, [owner, member, token, itemIndex])
            ?? throw new Xunit.Sdk.XunitException("FromContainer returned null.");
    }

    private static Type LeaseType() => typeof(AssetMemoryBudget).Assembly.GetType("Deviant.Browser.GmkInspectionLease")
        ?? throw new Xunit.Sdk.XunitException("GmkInspectionLease is missing from Deviant.Browser.");

    private static object InvokeMethod(MethodInfo method, object? target, object?[] args)
    {
        try { return method.Invoke(target, args)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static object? Invoke(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name}.{methodName} is missing.");
        return InvokeMethod(method, target, args);
    }

    private static object? Get(object target, string propertyName) =>
        target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static object? Get(object target, string nestedProperty, string property) =>
        Get(Required(target, nestedProperty), property);

    private static object Required(object target, string propertyName) =>
        Get(target, propertyName) ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name}.{propertyName} is null or missing.");

    private static object ResultValue(object? result) =>
        Required(result ?? throw new Xunit.Sdk.XunitException("The lease result is missing."), "Value");

    private static byte[] Bytes(object section)
    {
        var value = Required(section, "Bytes");
        return value switch
        {
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            byte[] bytes => bytes,
            _ => throw new Xunit.Sdk.XunitException("Inspection section bytes have an unexpected type.")
        };
    }

    private static void Dispose(object lease) =>
        (lease as IDisposable ?? throw new Xunit.Sdk.XunitException("GmkInspectionLease must be disposable.")).Dispose();

    private static ResourceIdentity Member(ArchiveSnapshot snapshot) =>
        new(snapshot.IndexContentId, Assert.Single(snapshot.Records, record => record.Type == 29).Ordinal,
            $"dc-object/0/4241/{NativeId}");

    private static FormatsFixtureBuilder CreateFixture(params (uint Type, uint Id, byte[] Payload)[] frames)
    {
        var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(29, ContainerId, QzipContainer(frames));
        fixture.WriteIndex();
        return fixture;
    }

    private static byte[] QzipContainer((uint Type, uint Id, byte[] Payload)[] objects)
    {
        using var stream = new MemoryStream();
        stream.Write("QZIP\0"u8);
        stream.Write("DC_INFO "u8);
        WriteUInt32(stream, 3);
        WriteUInt32(stream, checked((uint)(8 + objects.Length * 8)));
        WriteUInt32(stream, 0);
        WriteUInt32(stream, checked((uint)objects.Length));
        foreach (var item in objects) { WriteUInt32(stream, item.Type); WriteUInt32(stream, item.Id); }
        stream.Write("DC_DATA "u8);
        WriteUInt32(stream, 23);
        var bodyLength = objects.Sum(item => 13 + item.Payload.Length);
        WriteUInt32(stream, checked((uint)bodyLength));
        foreach (var item in objects)
        {
            WriteUInt32(stream, checked((uint)item.Payload.Length));
            stream.WriteByte(0);
            WriteUInt32(stream, 0);
            WriteUInt32(stream, 0);
            stream.Write(item.Payload);
        }
        return stream.ToArray();
    }

    private static byte[] GroupPayload()
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, 1);
        stream.Write("GMK_COMG"u8);
        WriteUInt32(stream, 22);
        WriteUInt32(stream, 3);
        WriteUInt32(stream, 2);
        WriteItem(stream, "Duplicate", "first text", [11, 22], 0, null, 101);
        WriteItem(stream, "Duplicate", "second text", [33], 1, (uint.MaxValue, 7), 202);
        return stream.ToArray();
    }

    private static void WriteItem(Stream stream, string name, string text, uint[] words, byte flag,
        (uint Word0, uint Word1)? pair, uint finalWord)
    {
        WriteText(stream, name);
        WriteText(stream, text);
        WriteUInt32(stream, checked((uint)words.Length));
        foreach (var word in words) WriteUInt32(stream, word);
        stream.WriteByte(flag);
        if (pair is { } reference) { WriteUInt32(stream, reference.Word0); WriteUInt32(stream, reference.Word1); }
        WriteUInt32(stream, finalWord);
    }

    private static void WriteText(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static byte[] PairBytes(uint first, uint second)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, first);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), second);
        return bytes;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
