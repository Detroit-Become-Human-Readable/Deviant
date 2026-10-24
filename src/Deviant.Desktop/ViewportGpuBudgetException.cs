namespace Deviant.Desktop;

internal sealed class ViewportGpuBudgetException(string category, long requested, long used, long limit)
    : InvalidOperationException($"The {category} exceeds the graphics memory limit.")
{
    public long RequestedBytes { get; } = requested;
    public long UsedBytes { get; } = used;
    public long LimitBytes { get; } = limit;
}
