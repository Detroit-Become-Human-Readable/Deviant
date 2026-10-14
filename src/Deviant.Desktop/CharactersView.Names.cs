using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Deviant.Assets;

namespace Deviant.Desktop;

public partial class CharactersView
{
    private void ClearNameEvidence()
    {
        NameStatus.Text = "";
        NameStatus.Visibility = Visibility.Collapsed;
        NamesList.ItemsSource = null;
        NamesExpander.IsExpanded = false;
        NamesExpander.Visibility = Visibility.Collapsed;
    }

    private void ShowNameEvidence(AssetDescriptor descriptor)
    {
        ClearNameEvidence();
        var hasNames = descriptor.Names.Any(name => !string.IsNullOrWhiteSpace(name.Text));
        var labelOrigins = descriptor.Names.Where(name => name.Text == descriptor.Label)
            .Select(name => name.Origin);
        var origins = labelOrigins.ToHashSet();
        var hasAlias = origins.Contains(NameOrigin.UserAlias);
        var conflict = descriptor.Metadata.TryGetValue("StoredNameConflict", out var storedConflict) &&
            string.Equals(storedConflict, "true", StringComparison.OrdinalIgnoreCase);
        NameStatus.Text = conflict
            ? hasAlias ? "Alias · multiple source names" : "Multiple source names"
            : hasAlias ? "Alias"
            : origins.Contains(NameOrigin.Verified) ? "Verified"
            : origins.Contains(NameOrigin.Stored) ? "Stored"
            : origins.Contains(NameOrigin.Description) ? "Label"
            : origins.Contains(NameOrigin.Candidate) ? "Candidate"
            : hasNames ? "Names available" : "Unnamed";
        NameStatus.Visibility = Visibility.Visible;
        if (!hasNames) return;
        NamesList.ItemsSource = descriptor.Names;
        NamesExpander.Visibility = Visibility.Visible;
    }
}

public sealed class NameOriginLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        NameOrigin.Stored => "Stored",
        NameOrigin.Verified => "Verified",
        NameOrigin.Candidate => "Candidate",
        NameOrigin.UserAlias => "Alias",
        NameOrigin.Description => "Label",
        _ => "Unknown"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
