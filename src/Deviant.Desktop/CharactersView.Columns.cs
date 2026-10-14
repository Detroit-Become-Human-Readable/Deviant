using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Deviant.Desktop;

public partial class CharactersView
{
    private void InitializeEntryColumns()
    {
        if (Entries.View is not GridView { Columns.Count: >= 2 } view) return;
        BindingOperations.SetBinding(view.Columns[1], GridViewColumn.WidthProperty, new Binding(nameof(ActualWidth))
        {
            Source = Entries,
            Converter = new AvailabilityColumnWidthConverter()
        });
        var defaultWidth = new MultiBinding
        {
            Mode = BindingMode.OneWay,
            Converter = new RemainingEntryWidthConverter()
        };
        defaultWidth.Bindings.Add(new Binding(nameof(ActualWidth)) { Source = Entries });
        defaultWidth.Bindings.Add(new Binding(nameof(GridViewColumn.Width)) { Source = view.Columns[1] });
        if (view.Columns.Count > 2) defaultWidth.Bindings.Add(new Binding(nameof(GridViewColumn.Width)) { Source = view.Columns[2] });
        BindingOperations.SetBinding(view.Columns[0], GridViewColumn.WidthProperty, defaultWidth);
    }

    private sealed class AvailabilityColumnWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double width && width >= 400 ? 105d : 0d;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class RemainingEntryWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var available = values.Length > 0 && values[0] is double width && double.IsFinite(width) ? width : 0;
            var metadata = values.Length > 1 && values[1] is double reserved && double.IsFinite(reserved) ? reserved : 105;
            if (values.Length > 2 && values[2] is double extra && double.IsFinite(extra)) metadata += extra;
            return Math.Clamp(available - metadata - 24, 64, 320);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}
