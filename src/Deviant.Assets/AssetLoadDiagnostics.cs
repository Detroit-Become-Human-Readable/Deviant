using System.Diagnostics;
using System.Collections.ObjectModel;

namespace Deviant.Assets;

/// <summary>Optional asset timings, file-read requests, and aggregate work counters.</summary>
public static class AssetLoadDiagnostics
{
    public const string ListenerName = "Deviant.AssetLoad";
    public const string MeasurementName = "Measurement";
    public const string CounterName = "Counters";
    public const int MaximumCounterFields = 32;
    public static DiagnosticListener Listener { get; } = new(ListenerName);
    public static bool CountersEnabled => Listener.IsEnabled(CounterName);

    public static IDisposable? Measure(string stage, string? key = null, string? relativeSource = null,
        long offset = 0, long requestedBytes = 0) => Listener.IsEnabled(MeasurementName)
        ? new Timing(stage, key, relativeSource, offset, requestedBytes) : null;

    public static void Cache(string stage, string key, bool hit)
    {
        if (Listener.IsEnabled(MeasurementName))
            Listener.Write(MeasurementName, new AssetLoadMeasurement(stage, key, 0, null, 0, 0, hit));
    }

    public static void Counters(string stage, string? key, IReadOnlyDictionary<string, long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!CountersEnabled) return;
        if (values.Count > MaximumCounterFields || values.Keys.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128))
            throw new ArgumentException("Diagnostic counters exceed the field limits.", nameof(values));
        var captured = new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(values, StringComparer.Ordinal));
        Listener.Write(CounterName, new AssetLoadCounterMeasurement(stage, key, captured));
    }

    private sealed class Timing(string stage, string? key, string? source, long offset, long bytes) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Listener.Write(MeasurementName, new AssetLoadMeasurement(stage, key,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, source, offset, bytes, null));
        }
    }
}

/// <summary>Elapsed scope time includes failed or cancelled work. File bytes are requested bytes, not physical device I/O.</summary>
public sealed record AssetLoadMeasurement(string Stage, string? Key, double ElapsedMilliseconds,
    string? RelativeSource, long Offset, long RequestedBytes, bool? CacheHit);

public sealed record AssetLoadCounterMeasurement(string Stage, string? Key, IReadOnlyDictionary<string, long> Values);
