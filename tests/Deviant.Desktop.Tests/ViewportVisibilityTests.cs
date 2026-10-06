using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class ViewportVisibilityTests
{
    [Fact]
    public void AuthoredHiddenObjectsRequireShowHidden()
    {
        var visibility = new OccurrenceVisibility();
        visibility.Add("visible", true); visibility.Add("hidden", false);
        Assert.False(visibility.ShowHidden);
        Assert.True(visibility.IsVisible("visible")); Assert.False(visibility.IsVisible("hidden"));
        Assert.True(visibility.IsManuallyVisible("hidden"));
        visibility.ShowHidden = true;
        Assert.True(visibility.IsVisible("visible")); Assert.True(visibility.IsVisible("hidden"));
        visibility.ShowHidden = false;
        Assert.False(visibility.IsVisible("hidden"));
    }

    [Fact]
    public void ShowHiddenNeverOverridesManualHides()
    {
        var visibility = new OccurrenceVisibility();
        visibility.Add("visible", true); visibility.Add("hidden", false);
        visibility.SetManualVisibility("visible", false); visibility.SetManualVisibility("hidden", false);
        visibility.ShowHidden = true;
        Assert.False(visibility.IsVisible("visible")); Assert.False(visibility.IsVisible("hidden"));
        visibility.SetManualVisibility("hidden", true);
        Assert.True(visibility.IsVisible("hidden")); Assert.False(visibility.IsVisible("visible"));
        visibility.ShowHidden = false;
        Assert.False(visibility.IsVisible("hidden"));
        visibility.ShowHidden = true;
        Assert.True(visibility.IsVisible("hidden")); Assert.False(visibility.IsVisible("visible"));
    }

    [Fact]
    public void UnloadDropsOccurrenceStateWhileKeepingTheViewPreference()
    {
        var visibility = new OccurrenceVisibility { ShowHidden = true };
        visibility.Add("item", true); visibility.SetManualVisibility("item", false);
        visibility.Clear();
        Assert.False(visibility.IsVisible("item")); Assert.True(visibility.ShowHidden);
        visibility.Add("item", false);
        Assert.True(visibility.IsVisible("item"));
        Assert.False(visibility.SetManualVisibility("missing", false));
    }
}
