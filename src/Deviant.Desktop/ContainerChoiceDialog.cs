using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Core;
namespace Deviant.Desktop;

internal sealed class ContainerChoiceDialog : Window
{
    private readonly ListBox rows=new(){DisplayMemberPath="Label",MinHeight=280};
    private readonly TextBox search=new(){Margin=new(0,10,0,10)};
    private readonly Choice[] choices;
    private ContainerChoiceDialog(ArchiveSnapshot snapshot)
    {
        Title="Choose container";Width=480;Height=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"WindowBackground");SetResourceReference(ForegroundProperty,"WindowForeground");
        choices=snapshot.Records.Where(row=>row.Type==29).Select((row,index)=>new Choice(new(snapshot.IndexContentId,row.Ordinal),$"Container {index+1}",row.Ordinal,row.Id)).ToArray();
        var panel=new DockPanel{Margin=new(16)};var header=new StackPanel();header.Children.Add(new TextBlock{Text="Search container label or resource ID"});header.Children.Add(search);DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        var open=new Button{Content="Inspect",HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,10,0,0),IsDefault=true};open.Click+=(_,_)=>{if(rows.SelectedItem is Choice)DialogResult=true;};DockPanel.SetDock(open,Dock.Bottom);panel.Children.Add(open);panel.Children.Add(rows);Content=panel;
        search.TextChanged+=(_,_)=>{var term=search.Text.Trim();rows.ItemsSource=choices.Where(item=>item.Label.Contains(term,StringComparison.OrdinalIgnoreCase)||item.Id.ToString().Equals(term,StringComparison.Ordinal)||item.Id.ToString("X8").Equals(term,StringComparison.OrdinalIgnoreCase)).Take(200).ToArray();};rows.ItemsSource=choices.Take(200).ToArray();
    }
    public static ResourceIdentity? Read(Window owner,ArchiveSnapshot snapshot){var dialog=new ContainerChoiceDialog(snapshot){Owner=owner};return dialog.ShowDialog()==true?(dialog.rows.SelectedItem as Choice)?.Identity:null;}
    private sealed record Choice(ResourceIdentity Identity,string Label,long Ordinal,uint Id);
}
