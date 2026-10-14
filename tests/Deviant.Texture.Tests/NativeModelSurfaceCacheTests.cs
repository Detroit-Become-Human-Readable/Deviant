using System.Reflection;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeModelSurfaceCacheTests
{
    [Fact]
    public async Task EquivalentAliasesShareOneDecodeAndRetainPixelsUntilTheLastLease()
    {
        var (cache, acquire) = CreateCache();
        var budget = new AssetMemoryBudget(1024);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<SurfacePixels>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<CancellationToken, Task<SurfacePixels>> factory = async token =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(token);
            return await complete.Task.WaitAsync(token);
        };

        var first = Acquire(cache, acquire, "same-verified-source", factory, CancellationToken.None);
        _ = await entered.Task;
        var second = Acquire(cache, acquire, "same-verified-source", factory, CancellationToken.None);
        var pixels = new SurfacePixels(budget.Reserve(64, "generated shared pixels"));
        complete.SetResult(pixels);

        using var firstLease = await first;
        using var secondLease = await second;
        Assert.Equal(1, calls);
        Assert.Same(pixels, Value(firstLease));
        Assert.Same(pixels, Value(secondLease));
        Assert.Equal(64, budget.UsedBytes);

        firstLease.Dispose();
        Assert.Equal(64, budget.UsedBytes);
        secondLease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancellingOneAliasDoesNotCancelAnotherAndFailedDecodeIsEvicted()
    {
        var (cache, acquire) = CreateCache();
        var budget = new AssetMemoryBudget(1024);
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<SurfacePixels>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<SurfacePixels>> waitingFactory = async token =>
        {
            entered.TrySetResult(token);
            return await complete.Task.WaitAsync(token);
        };

        var abandoned = Acquire(cache, acquire, "shared-alias", waitingFactory, cancelled.Token);
        var sharedToken = await entered.Task;
        var remaining = Acquire(cache, acquire, "shared-alias", waitingFactory, CancellationToken.None);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.False(sharedToken.IsCancellationRequested);

        var pixels = new SurfacePixels(budget.Reserve(32, "generated shared pixels"));
        complete.SetResult(pixels);
        using (var lease = await remaining) Assert.Same(pixels, Value(lease));
        Assert.Equal(0, budget.UsedBytes);

        var attempts = 0;
        Func<CancellationToken, Task<SurfacePixels>> failOnce = _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return Task.FromException<SurfacePixels>(new InvalidOperationException("generated decode failure"));
            return Task.FromResult(new SurfacePixels(budget.Reserve(16, "generated retry pixels")));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Acquire(cache, acquire, "failed-source", failOnce, CancellationToken.None));
        using (var retry = await Acquire(cache, acquire, "failed-source", failOnce, CancellationToken.None))
            Assert.IsType<SurfacePixels>(Value(retry));
        Assert.Equal(2, attempts);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancellingTheFinalWaiterDrainsProducerAndReleasesItsReservation()
    {
        var (cache, acquire) = CreateCache();
        var budget = new AssetMemoryBudget(1024);
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<SurfacePixels>> factory = async token =>
        {
            using var reservation = budget.Reserve(64, "generated cancelled pixels");
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The cancelled producer unexpectedly completed.");
        };

        var pending = Acquire(cache, acquire, "cancelled-source", factory, cancelled.Token);
        await entered.Task;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void PixelKeyIgnoresAliasIdentityButSeparatesSourceAndPreviewChoices()
    {
        var source = ConvertedTextureFixture.Source(soleRaw: true, selector: 21, width: 32, height: 32);
        var alias = source with
        {
            NativeTextureIdentity = new(source.SelectedIdentity.IndexContentId, source.SelectedIdentity.Ordinal + 100)
        };
        var key = SurfaceKey(source, 16);

        Assert.Equal(key, SurfaceKey(alias, 16));
        Assert.NotEqual(key, SurfaceKey(source, 8));
        var nextRevision = new SourceRevision(source.Revision.IndexContentId, "generated-next-revision");
        Assert.NotEqual(key, SurfaceKey(source with
        {
            Revision = nextRevision,
            Metadata = source.Metadata with { Revision = nextRevision }
        }, 16));
        Assert.NotEqual(key, SurfaceKey(ConvertedTextureFixture.Source(soleRaw: true, selector: 7, width: 32, height: 32), 16));
        Assert.NotEqual(key, SurfaceKey(source with
        {
            Payloads = source.Payloads.Select(payload => payload with { Sha256 = new string('0', 64) }).ToArray()
        }, 16));
    }

    private static (object Cache, MethodInfo Acquire) CreateCache()
    {
        var openType = typeof(NativeModelSession).Assembly.GetType("Deviant.Browser.NativeModelSurfaceCache`1");
        Assert.NotNull(openType);
        var closedType = openType!.MakeGenericType(typeof(SurfacePixels));
        var acquire = closedType.GetMethod("AcquireAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(acquire);
        return (Activator.CreateInstance(closedType, nonPublic: true)!, acquire!);
    }

    private static async Task<IDisposable> Acquire(object cache, MethodInfo method, string key,
        Func<CancellationToken, Task<SurfacePixels>> factory, CancellationToken token)
    {
        var pending = Assert.IsAssignableFrom<Task>(method.Invoke(cache, [key, factory, token]));
        await pending.ConfigureAwait(false);
        return Assert.IsAssignableFrom<IDisposable>(pending.GetType().GetProperty("Result")!.GetValue(pending));
    }

    private static object Value(IDisposable lease) =>
        lease.GetType().GetProperty("Value")!.GetValue(lease)!;

    private static string SurfaceKey(NativeTextureSource source, int previewDimension)
    {
        var method = typeof(NativeModelSession).GetMethod("CreateSurfacePreparationKey",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [source, previewDimension]));
    }

    private sealed class SurfacePixels(AssetMemoryBudget.Reservation reservation) : IDisposable
    {
        public void Dispose() => reservation.Dispose();
    }
}
