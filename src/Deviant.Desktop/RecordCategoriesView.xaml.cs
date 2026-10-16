using System.Windows;
using System.Windows.Controls;

namespace Deviant.Desktop;

internal partial class RecordCategoriesView : UserControl
{
    public RecordCategoriesView() => InitializeComponent();

    public event Action<NavigationItem>? OpenCategoryRequested;

    public void SetCategories(IEnumerable<NavigationItem> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var items = categories.ToArray();
        CategoryButtons.ItemsSource = items;
        CategoryButtons.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavigationItem category })
            OpenCategoryRequested?.Invoke(category);
    }
}
