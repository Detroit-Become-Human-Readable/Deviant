using System.Reflection;
using Deviant.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class OggValidationFailureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-ogg-validation-" + Guid.NewGuid().ToString("N"));

    public OggValidationFailureTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("native-cleanup")]
    [InlineData("data-cleanup")]
    [InlineData("resource")]
    public async Task ProducerAndCleanupFailuresKeepTheOriginalExceptionAndStructuredDetails(string kind)
    {
        Exception expected;
        string tag;
        switch (kind)
        {
            case "native-cleanup":
                expected = new AudioToolException("Generated native exit failure");
                expected.Data["NativeExitCode"] = 17;
                tag = "ScratchCleanupError";
                break;
            case "data-cleanup":
                expected = new InvalidDataException("Generated invalid capture");
                tag = "AudioScratchCleanupError";
                break;
            default:
                expected = new AudioToolException("Generated binary output limit");
                tag = "ProcessCleanup";
                break;
        }
        expected.Data[tag] = "Generated structured producer/cleanup diagnostic";
        expected.Data["ErrorCode"] = "GeneratedValidationFailure";
        expected.Data["Sentinel"] = 314159;
        var storage = new RejectingScratchStorage(expected);

        var actual = await Record.ExceptionAsync(async () => { _ = await InvokeValidationAsync(storage); });

        Assert.Same(expected, actual);
        Assert.Equal("Generated structured producer/cleanup diagnostic", actual!.Data[tag]);
        Assert.Equal("GeneratedValidationFailure", actual.Data["ErrorCode"]);
        Assert.Equal(314159, actual.Data["Sentinel"]);
        if (kind == "native-cleanup") Assert.Equal(17, actual.Data["NativeExitCode"]);
        Assert.Equal(1, storage.ScratchRequests);
        Assert.Equal(0, storage.SourceRequests);
        Assert.Equal(".wav", storage.RequestedExtension);
        Assert.Empty(Directory.EnumerateFiles(root, "*.wav"));
    }

    [Fact]
    public async Task UntaggedInvalidDataRemainsAnInvalidCandidateResult()
    {
        var expected = new InvalidDataException("Generated malformed candidate");
        var storage = new RejectingScratchStorage(expected);

        var result = await InvokeValidationAsync(storage);

        Assert.NotNull(result);
        Assert.Null(Property(result, "Metadata"));
        Assert.Null(Property(result, "Stream"));
        Assert.Equal(expected.Message, Property(result, "Diagnostic"));
        Assert.Equal(1, storage.ScratchRequests);
        Assert.Equal(0, storage.SourceRequests);
    }

    private async Task<object?> InvokeValidationAsync(RejectingScratchStorage storage)
    {
        var input = Path.Combine(root, "generated.ogg");
        await File.WriteAllBytesAsync(input, new byte[27]);
        // This path deliberately cannot start a native tool. The fake rejects before invoking
        // its producer; an attempt to bypass it would produce a different exception and fail.
        var missingTool = Path.Combine(root, "never-invoked-decoder");
        Assert.False(File.Exists(missingTool));
        var service = new AudioService(new AudioToolPaths(null, missingTool, null, null, null, null, null), sourceStorage: storage);
        var method = typeof(AudioService).GetMethod("DecodeOggForValidationAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(string), typeof(string), typeof(CancellationToken)], modifiers: null)!;
        var operation = (Task)method.Invoke(service, [input, root, CancellationToken.None])!;
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        return operation.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)!.GetValue(operation);
    }

    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class RejectingScratchStorage(Exception failure) : IAudioSourceStorage, IAudioScratchStorage
    {
        public int ScratchRequests { get; private set; }
        public int SourceRequests { get; private set; }
        public string? RequestedExtension { get; private set; }

        public Task<AudioSourceLease> AcquireAsync(AudioSourceKey source, Func<Stream, CancellationToken, Task> producer,
            CancellationToken cancellationToken = default)
        {
            SourceRequests++;
            throw new InvalidOperationException("Candidate validation must not materialize another source.");
        }

        public Task<AudioSourceLease> CreateAsync(string extension, Func<Stream, CancellationToken, Task> producer,
            Func<string, CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
        {
            ScratchRequests++;
            RequestedExtension = extension;
            ArgumentNullException.ThrowIfNull(producer);
            // Reject synchronously before calling the provided native producer or validator.
            throw failure;
        }
    }
}
