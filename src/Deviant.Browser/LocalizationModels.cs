using Deviant.Assets;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

public sealed record LocalizationSourceChoice(ResourceIdentity Identity, string Label, string RelativeName,
    uint Package, long RecordCount);
