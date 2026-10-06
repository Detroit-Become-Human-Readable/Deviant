using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Deviant.Assets;
using Deviant.Graphs;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class GraphExportIntegrationTests
{
    [Fact]
    public async Task JsonAndSvgOmitPrivatePathsFromLabelsAndEvidence()
    {
        var revision=new SourceRevision("index","revision");
        var identity=new ResourceIdentity("index",1);
        var localPath=Path.Combine(Path.GetTempPath(),"graph session "+Guid.NewGuid().ToString("N"),"member.bin");
        var asset=new AssetDescriptor(identity,revision,"resource",localPath,null,AssetAvailability.Available,
            AssetCompleteness.Unverified,AssetCapabilities.None,[],[new(localPath,10,20)])
        {Metadata=new Dictionary<string,string>{{"description",localPath}}};
        var relationship=new AssetRelationship(identity,AssetRelationshipKind.Dependency,"Observation",new(IdentifierNamespace.Archive,"2"),
            new(ReferenceStatus.Missing,[],"Unavailable"),new(false,localPath,"Read from "+localPath));
        var graph=RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences,[asset],[relationship]);
        await using var jsonStream=new MemoryStream();
        await EvidenceGraphExport.WriteJsonAsync(graph,revision,jsonStream);
        using var json=JsonDocument.Parse(jsonStream.ToArray());
        var jsonValues=StringValues(json.RootElement).ToArray();
        Assert.DoesNotContain(jsonValues,value=>value.Contains(localPath,StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(jsonValues,value=>value.Contains("graph session",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(jsonValues,value=>value=="member.bin");
        await using var svgStream=new MemoryStream();
        await EvidenceGraphExport.WriteSvgAsync(graph,revision,svgStream);
        svgStream.Position=0;var svg=XDocument.Load(svgStream);
        var svgValues=svg.Descendants().Select(element=>element.Value).Concat(svg.Descendants().Attributes().Select(attribute=>attribute.Value));
        Assert.DoesNotContain(svgValues,value=>value.Contains(localPath,StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(svgValues,value=>value.Contains("graph session",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SvgEscapesMarkupAndKeepsResourceGraphDomain()
    {
        var revision=new SourceRevision("index","revision");
        var identity=new ResourceIdentity("index",1);
        var asset=new AssetDescriptor(identity,revision,"resource","<script>alert('x')</script> & Café",null,AssetAvailability.Available,
            AssetCompleteness.Unverified,AssetCapabilities.None,[],[]);
        var graph=RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences,[asset],[]);
        await using var stream=new MemoryStream();await EvidenceGraphExport.WriteSvgAsync(graph,revision,stream);
        stream.Position=0;var svg=XDocument.Load(stream);XNamespace ns="http://www.w3.org/2000/svg";
        Assert.Empty(svg.Descendants(ns+"script"));
        Assert.Contains(svg.Descendants(ns+"text"),element=>element.Value.Contains("ArchiveResourceReferences",StringComparison.Ordinal));
        Assert.DoesNotContain(svg.Descendants(ns+"text"),element=>element.Value.Contains("NarrativeStructure",StringComparison.Ordinal));
    }

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.String){yield return element.GetString()!;yield break;}
        if(element.ValueKind==JsonValueKind.Array){foreach(var item in element.EnumerateArray())foreach(var value in StringValues(item))yield return value;}
        else if(element.ValueKind==JsonValueKind.Object){foreach(var property in element.EnumerateObject()){yield return property.Name;foreach(var value in StringValues(property.Value))yield return value;}}
    }
}
