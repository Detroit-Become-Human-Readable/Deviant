namespace Deviant.Export.Models;

/// <summary>Indicates that a surface needs separate output UV coordinates for baking.</summary>
public sealed class MaterialBakeAtlasRequiredException(string message) : IOException(message);
