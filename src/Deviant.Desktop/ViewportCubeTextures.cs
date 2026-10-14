using Deviant.Browser;
using Deviant.Formats.Textures;
using HelixToolkit.SharpDX;

namespace Deviant.Desktop;

public sealed partial class AssetViewportService
{
    internal TextureLease AcquireCubeTexture(RenderAssetSnapshot snapshot, DdsTextureDocument document,
        CancellationToken token = default)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        token.ThrowIfCancellationRequested();
        var key = TextureContentKey.CreateCube(document, snapshot.Revision);
        var retained = textures.Acquire(key, () =>
        {
            var gpu = ReserveGpu(document.Info.EncodedLength, "cube mip buffers");
            AssetMemoryBudget.Reservation? cpu = null;
            IDisposable? sourceLease = null;
            RawTextureLoader? loader = null;
            try
            {
                token.ThrowIfCancellationRequested();
                cpu = snapshot.MemoryBudget.Reserve(document.OriginalBytes.Length, "cube DDS upload");
                sourceLease = snapshot.AcquireLease();
                loader = RawTextureLoader.CreateCube(document, snapshot.Revision);
                token.ThrowIfCancellationRequested();
                var model = new TextureModel(Guid.NewGuid(), loader);
                return new TextureEntry(model, loader, cpu, gpu, sourceLease);
            }
            catch
            {
                loader?.Dispose(); sourceLease?.Dispose(); cpu?.Dispose(); gpu.Dispose();
                throw;
            }
        });
        try
        {
            token.ThrowIfCancellationRequested();
            return new(this, retained);
        }
        catch { retained.Dispose(); throw; }
    }
}
