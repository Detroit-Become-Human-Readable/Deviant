using Xunit;

namespace Deviant.Desktop.Tests;

// WPF's application resource packages are shared across these STA checks.
[CollectionDefinition("WPF",DisableParallelization=true)]
public sealed class WpfTestCollection;
