using Deviant.Assets;

namespace Deviant.Desktop;

internal sealed record AssetSortChoice(string Label,AssetSort Sort)
{
    public override string ToString()=>Label;
}
