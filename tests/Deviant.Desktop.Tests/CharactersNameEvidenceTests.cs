using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Deviant.Assets;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersNameEvidenceTests
{
    [Fact]
    public void OriginTagsRemainVisibleForLongNamesAtCompactWidth()
    {
        RunSta(() =>
        {
            var view = new CharactersView();
            var window = new Window { Content = view, Width = 760, Height = 580,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                Select(view, Descriptor(new string('x', 200), [
                    new(new string('x', 200), NameOrigin.Stored, "generated source"),
                    new(new string('y', 200), NameOrigin.Stored, "generated second source")]));
                Control<Expander>(view, "NamesExpander").IsExpanded = true;
                window.UpdateLayout();
                var list = Control<ListBox>(view, "NamesList");
                var origins = Descendants(list).OfType<TextBlock>().Where(text => text.Text == "Stored").ToArray();
                Assert.Equal(2, origins.Length);
                foreach (var origin in origins)
                {
                    var position = origin.TransformToAncestor(list).Transform(new Point());
                    Assert.InRange(position.X, 0, list.ActualWidth);
                    Assert.True(position.X + origin.ActualWidth <= list.ActualWidth,
                        "A name's origin tag was pushed outside the compact list.");
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AliasAndConflictingOriginalNamesRemainVisibleWithoutChangingTheDescriptor()
    {
        RunSta(() =>
        {
            var view = new CharactersView();
            var entry = Descriptor("My model", [
                new("mesh_alpha", NameOrigin.Stored, "generated mesh A"),
                new("mesh_beta", NameOrigin.Stored, "generated mesh B"),
                new("My model", NameOrigin.UserAlias, "generated alias")]) with
            { Metadata = new Dictionary<string, string> { ["StoredNameConflict"] = "true" } };
            Select(view, entry);

            Assert.Same(entry, view.SelectedDescriptor);
            Assert.Equal("My model", Control<TextBlock>(view, "SelectedName").Text);
            Assert.Equal("Alias · multiple source names", Control<TextBlock>(view, "NameStatus").Text);
            Assert.Equal(Visibility.Visible, Control<Expander>(view, "NamesExpander").Visibility);
            Assert.Same(entry.Names, Control<ListBox>(view, "NamesList").ItemsSource);
            var names = Control<ListBox>(view, "NamesList").Items.Cast<NameEvidence>().ToArray();
            Assert.Equal(new[] { "mesh_alpha", "mesh_beta", "My model" }, names.Select(row => row.Text));
            var origins = Assert.IsAssignableFrom<IValueConverter>(view.Resources["NameOriginLabel"]);
            Assert.Equal(new[] { "Stored", "Stored", "Alias" }, names.Select(row => origins.Convert(row.Origin,
                typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Contains("generated mesh A", Control<TextBox>(view, "Advanced").Text);
            Assert.Contains("generated mesh B", Control<TextBox>(view, "Advanced").Text);
        });
    }

    [Fact]
    public void PrefixAndCandidateLabelsAreNotPresentedAsVerifiedNames()
    {
        RunSta(() =>
        {
            var view = new CharactersView();
            var prefix = Descriptor("model_prefix", [new("model_prefix", NameOrigin.Description, "generated prefix")]);
            Select(view, prefix);
            Assert.Equal("Label", Control<TextBlock>(view, "NameStatus").Text);
            Assert.Equal(NameOrigin.Description, Assert.IsType<NameEvidence>(Control<ListBox>(view, "NamesList").Items[0]).Origin);
            var candidate = Descriptor("possible_name", [new("possible_name", NameOrigin.Candidate, "generated candidate")]);
            Select(view, candidate);
            Assert.Equal("Candidate", Control<TextBlock>(view, "NameStatus").Text);
            Assert.Equal(NameOrigin.Candidate, Assert.IsType<NameEvidence>(Control<ListBox>(view, "NamesList").Items[0]).Origin);
        });
    }

    [Fact]
    public void ReselectionAndClearReleasePreviousNamesAndCollapseTheInspector()
    {
        RunSta(() =>
        {
            var view = new CharactersView();
            Select(view, Descriptor("mesh_alpha", [new("mesh_alpha", NameOrigin.Stored, "generated source")]));
            Control<Expander>(view, "NamesExpander").IsExpanded = true;
            Select(view, Descriptor("Model 2", []));
            Assert.Equal("Unnamed", Control<TextBlock>(view, "NameStatus").Text);
            Assert.Empty(Control<ListBox>(view, "NamesList").Items);
            Assert.False(Control<Expander>(view, "NamesExpander").IsExpanded);
            Assert.Equal(Visibility.Collapsed, Control<Expander>(view, "NamesExpander").Visibility);
            view.Clear();
            Assert.Equal("", Control<TextBlock>(view, "NameStatus").Text);
            Assert.Null(Control<ListBox>(view, "NamesList").ItemsSource);
        });
    }

    private static AssetDescriptor Descriptor(string label, IReadOnlyList<NameEvidence> names) =>
        new(new("generated-index", 1), new("generated-index", "generated-revision"), "character-model",
            label, null, AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.None, names, []);

    private static void Select(CharactersView view, AssetDescriptor entry)
    {
        view.SetEntries([entry], entry.Revision);
        var entries = Control<ListView>(view, "Entries");
        entries.SelectedItem = entries.Items[0];
    }

    private static T Control<T>(CharactersView view, string name) where T : FrameworkElement =>
        Assert.IsType<T>(view.FindName(name));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Name inspection did not finish.");
        if (failure is not null) throw new InvalidOperationException("Name inspection failed.", failure);
    }
}
