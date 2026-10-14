using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class AssetMemoryCategoryTests
{
    [Fact]
    public void CategoriesFollowSharedReservationsReductionAndIdempotentRelease()
    {
        var budget = new AssetMemoryBudget(100);
        using var first = budget.Reserve(20, "geometry");
        using var second = budget.Reserve(30, "geometry");
        using var texture = budget.Reserve(10, "textures");
        var snapshot = budget.CategoryUsage;
        Assert.Equal(50, snapshot["geometry"]);
        second.ReduceTo(5);
        Assert.Equal(50, snapshot["geometry"]);
        Assert.Equal(25, budget.CategoryUsage["geometry"]);
        first.Dispose(); first.Dispose();
        Assert.Equal(budget.UsedBytes, budget.CategoryUsage.Values.Sum());
        second.Dispose(); texture.Dispose();
        Assert.Empty(budget.CategoryUsage);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void RejectedAdmissionKeepsItsBreakdownAfterOutstandingWorkDrains()
    {
        var budget = new AssetMemoryBudget(100);
        using var held = budget.Reserve(90, "source metadata");
        var error = Assert.Throws<ArchiveException>(() => budget.Reserve(11, "decode scratch"));
        var admission = Assert.IsType<AssetMemoryAdmission>(error.Data["MemoryAdmission"]);
        held.Dispose();
        Assert.Equal(11, admission.RequestedBytes);
        Assert.Equal(90, admission.UsedBytes);
        Assert.Equal(100, admission.MaximumBytes);
        Assert.Equal(90, admission.Categories["source metadata"]);
        Assert.False(admission.Categories.ContainsKey("decode scratch"));
        Assert.Empty(budget.CategoryUsage);
    }
}
