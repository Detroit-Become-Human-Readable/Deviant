using System.Globalization;
using System.Windows;
using Deviant.Browser;
using Deviant.Core;
using Microsoft.Win32;

namespace Deviant.Desktop;

public partial class ExportReviewWindow : Window
{
    private readonly ExportReview review;
    public ExportOptions? Options { get; private set; }
    public string ResourceCount => review.Records.Count.ToString("N0", CultureInfo.InvariantCulture);
    public string Bytes => review.Bytes.ToString("N0", CultureInfo.InvariantCulture);
    public string InvalidCount => review.InvalidCount.ToString("N0", CultureInfo.InvariantCulture);

    public ExportReviewWindow(ExportReview review)
    {
        this.review = review ?? throw new ArgumentNullException(nameof(review));
        InitializeComponent();
        DataContext = this;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose an export folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) OutputFolderTextBox.Text = dialog.FolderName;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var output = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            MessageBox.Show(this, "Choose an export folder.", "Export folder required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Options = new ExportOptions(output, OverwriteCheckBox.IsChecked == true);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
