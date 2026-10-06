using System.Windows;
using System.Windows.Input;
using Deviant.Assets;

namespace Deviant.Desktop;

public partial class AssetFamilyView
{
    public void SetRelationshipStatus(ResourceIdentity identity,string status)
    {if(SelectedIdentity==identity){RelatedPanel.Visibility=Visibility.Visible;RelatedStatus.Text=status;}}

    public void SetRelationships(ResourceIdentity identity,IReadOnlyList<AssetFamilyRelatedChoice> rows,string status,string evidence="")
    {
        if(SelectedIdentity!=identity)return;
        RelatedPanel.Visibility=Visibility.Visible;
        RelatedRows.ItemsSource=rows;
        RelatedStatus.Text=status;
        RelatedEvidence.Text=evidence;
    }

    public async Task NavigateSelectedRelationshipAsync()
    {
        if(RelatedRows.SelectedItem is not AssetFamilyRelatedChoice {CanNavigate:true} row||RelatedNavigator is null)return;
        await RelatedNavigator(row.Key);
    }

    private async void Related_DoubleClick(object sender,MouseButtonEventArgs e)
    {await OpenRelationshipAsync();}

    private async void OpenRelated_Click(object sender,RoutedEventArgs e)
    {await OpenRelationshipAsync();}

    private async void Related_KeyDown(object sender,KeyEventArgs e)
    {if(e.Key==Key.Enter&&RelatedRows.SelectedItem is AssetFamilyRelatedChoice {CanNavigate:true}){e.Handled=true;await OpenRelationshipAsync();}}

    private async Task OpenRelationshipAsync()
    {
        var identity=SelectedIdentity;var revision=LoadedRevision;var version=SelectionVersion;
        try{await NavigateSelectedRelationshipAsync();}
        catch(OperationCanceledException){}
        catch(Exception error){if(identity==SelectedIdentity&&revision==LoadedRevision&&version==SelectionVersion)RelatedStatus.Text=error.Message;}
    }
}
