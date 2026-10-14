namespace Deviant.Desktop;

public sealed record WorkspaceActivityProgress(bool IsActive, string Stage, long Completed = 0, long Total = 0);
