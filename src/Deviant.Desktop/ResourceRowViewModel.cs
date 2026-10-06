using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Deviant.Core;

namespace Deviant.Desktop;

public sealed class ResourceRowViewModel(ResourceRecord resource, bool isSelected,
    Action<long, bool>? selectionChanged = null) : INotifyPropertyChanged
{
    private bool selected = isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ResourceRecord Resource { get; private set; } = resource;
    public long Ordinal => Resource.Ordinal;
    public string Id => $"{Resource.Id.ToString(CultureInfo.InvariantCulture)} / 0x{Resource.Id:X8}";
    public string Type => $"{Resource.Type.ToString(CultureInfo.InvariantCulture)} / 0x{Resource.Type:X8}";
    public string Flags => $"0x{Resource.Flags:X8}";
    public string Package => Resource.Package.ToString(CultureInfo.InvariantCulture);
    public string Volume => Resource.VolumeName;
    public string Offset => $"0x{Resource.Offset:X} ({Resource.Offset.ToString("N0", CultureInfo.InvariantCulture)})";
    public string StoredSize => Resource.StoredSize.ToString("N0", CultureInfo.InvariantCulture);
    public string AlternativeSize => $"{Resource.AlternativeSize.ToString(CultureInfo.InvariantCulture)} / 0x{Resource.AlternativeSize:X8}";
    public string Validity => Resource.Validity.ToString();

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            OnPropertyChanged();
            selectionChanged?.Invoke(Resource.Ordinal, value);
        }
    }

    public void UpdateSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        OnPropertyChanged(nameof(IsSelected));
    }

    public void UpdateResource(ResourceRecord value, bool isSelected, bool notify = true)
    {
        Resource = value; selected = isSelected;
        if (notify) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ResourceRowCollection : ObservableCollection<ResourceRowViewModel>
{
    public void ReplaceAll(IEnumerable<ResourceRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
