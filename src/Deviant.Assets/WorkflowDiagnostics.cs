namespace Deviant.Assets;

public static class WorkflowDiagnostics
{
    public static LocalDiagnosticLog? Current { get; set; }
    public static WorkflowDiagnosticOperation? Begin(string operation, string? assetKey = null, long? generation = null,
        string? materialKey = null) => Current?.Begin(operation, assetKey, generation, materialKey);
    public static void Failure(string operation, Exception error, string? assetKey = null, bool partial = false, string? phase = null)
    {
        using var scope = Begin(operation, assetKey, materialKey: error.Data["MaterialIdentity"]?.ToString());
        scope?.Progress(phase ?? "Failure");
        scope?.Fail(error, error is Deviant.Core.ArchiveException archive ? archive.Code : null, partial: partial);
    }
}
