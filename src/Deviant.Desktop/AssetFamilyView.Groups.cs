using System.Windows;
using Deviant.Assets;

namespace Deviant.Desktop;

public partial class AssetFamilyView
{
    public async Task SetGroupsAsync(IEnumerable<AssetFamilyGroupChoice> choices,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(snapshot is not{}source)return;
        var selected=(Groups.SelectedItem as AssetFamilyGroupChoice)?.Key??"";
        var selectedIdentity=SelectedIdentity;
        var previousMembers=groupMembers.GetValueOrDefault(selected);
        var groups=choices.ToArray();var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var group in groups)
        {
            token.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(group.Key)||!keys.Add(group.Key))throw new ArgumentException("Each asset group needs a unique key.",nameof(choices));
            if(group.Members.Any(member=>member.IndexContentId!=source.IndexContentId||member.MemberPath is not null))throw new ArgumentException("Asset groups must refer to this archive.",nameof(choices));
        }
        updating=true;
        try
        {
            groupMembers.Clear();foreach(var group in groups)groupMembers[group.Key]=group.Members.ToHashSet();
            Groups.ItemsSource=new[]{new AssetFamilyGroupChoice("","All assets",[])}.Concat(groups).ToArray();
            Groups.SelectedItem=Groups.Items.Cast<AssetFamilyGroupChoice>().SingleOrDefault(group=>group.Key==selected)??Groups.Items[0];
            Groups.Visibility=groups.Length==0?Visibility.Collapsed:Visibility.Visible;
        }
        finally{updating=false;}
        if(selected.Length>0)
        {
            var next=(Groups.SelectedItem as AssetFamilyGroupChoice)?.Key??"";
            var nextMembers=groupMembers.GetValueOrDefault(next);
            if(next==selected&&previousMembers is not null&&nextMembers is not null&&previousMembers.SetEquals(nextMembers))return;
            var expectedQuery=queryGeneration+1;
            var expectedSelection=selectionGeneration+1+(next!=renderedGroupKey?1:0);
            await RefreshAsync(token);
            token.ThrowIfCancellationRequested();
            if(queryGeneration!=expectedQuery||selectionGeneration!=expectedSelection)return;
            if(selectedIdentity is not null&&ReferenceEquals(source,snapshot)&&((Groups.SelectedItem as AssetFamilyGroupChoice)?.Key??"")==next)
            {
                var index=Array.FindIndex(matches,item=>item.Identity==selectedIdentity);
                if(index>=0){pageOffset=index/PageSize*PageSize;RenderPage();Rows.SelectedItem=matches[index];}
            }
        }
    }
    public async Task SelectGroupAsync(string key,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var choice=Groups.Items.Cast<AssetFamilyGroupChoice>().SingleOrDefault(group=>group.Key==key)
            ??throw new InvalidOperationException("The catalog group is unavailable.");
        updating=true;try{Groups.SelectedItem=choice;}finally{updating=false;}
        await RefreshAsync(token);
        token.ThrowIfCancellationRequested();
    }
    public IReadOnlyDictionary<string,string> CaptureFilter()=>new Dictionary<string,string>
    { ["search"]=Search.Text,["sort"]=Sort.SelectedIndex.ToString(),["type"]=TypeFilter.Text,["id"]=IdFilter.Text,["group"]=(Groups.SelectedItem as AssetFamilyGroupChoice)?.Key??"" };
    public async Task ApplyFilterAsync(IReadOnlyDictionary<string,string> values,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var key=values.GetValueOrDefault("group","");
        var choice=key.Length==0?Groups.Items.Cast<AssetFamilyGroupChoice>().SingleOrDefault(group=>group.Key==""):
            Groups.Items.Cast<AssetFamilyGroupChoice>().SingleOrDefault(group=>group.Key==key)??throw new InvalidOperationException("The saved catalog group is unavailable.");
        if(!int.TryParse(values.GetValueOrDefault("sort","0"),out var sort)||sort<0||sort>=Sort.Items.Count)throw new InvalidOperationException("The saved sort is unavailable.");
        updating=true;try{Search.Text=values.GetValueOrDefault("search","");TypeFilter.Text=values.GetValueOrDefault("type","");IdFilter.Text=values.GetValueOrDefault("id","");Sort.SelectedIndex=sort;Groups.SelectedItem=choice;}finally{updating=false;}
        await RefreshAsync(token);
        token.ThrowIfCancellationRequested();
    }
}
