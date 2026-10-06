using System.Buffers.Binary;
using System.IO;
using System.Text;
using Deviant.Core;

namespace Deviant.Desktop.Tests;

internal sealed class DiscoveryFixture:IDisposable
{
    public string Root{get;}=Path.Combine(Path.GetTempPath(),"deviant-discovery-"+Guid.NewGuid().ToString("N"));
    public string Index{get;}
    public string Cache=>Path.Combine(Root,"cache");
    public string Library=>Path.Combine(Root,"library");
    public DiscoveryFixture(int count=220)
    {
        var source=Path.Combine(Root,"source");Directory.CreateDirectory(source);Index=Path.Combine(source,"archive.idx");
        var entries=Enumerable.Range(1,count).Select(number=>(Type:1033u,Bytes:Dialogue(number))).ToList();
        entries.Add((1016,Localization(count)));
        WriteIndex(Index,entries);
    }
    internal static void WriteIndex(string path,IReadOnlyList<(uint Type,byte[] Bytes)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var index=new byte[IndexLayout.HeaderLength+entries.Count*IndexLayout.RecordLength];IndexLayout.Magic.CopyTo(index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20),IndexLayout.SupportedVersion);BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101),(uint)entries.Count);
        using var volume=new MemoryStream();for(var ordinal=0;ordinal<entries.Count;ordinal++){var entry=entries[ordinal];uint[] values=[entry.Type,1,(uint)ordinal+1,(uint)volume.Length,(uint)entry.Bytes.Length,0,0];for(var field=0;field<values.Length;field++)BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength+ordinal*IndexLayout.RecordLength+field*4),values[field]);volume.Write(entry.Bytes);}
        File.WriteAllBytes(path,index);File.WriteAllBytes(Path.ChangeExtension(path,".dat"),volume.ToArray());
    }
    internal static void WriteLinkedStrings(string path)
    {
        using var records=new MemoryStream();using(var writer=new BinaryWriter(records,Encoding.UTF8,true)){var key="DIALOG_KEY"u8.ToArray();var text=Encoding.Unicode.GetBytes("Localized line");writer.Write(1u);writer.Write((uint)key.Length);writer.Write(key);writer.Write((uint)text.Length);writer.Write(text);writer.Write(1u);writer.Write(1u);writer.Write((uint)key.Length);writer.Write(key);writer.Write((byte)1);writer.Write((byte)0xA5);writer.Write(4091u);writer.Write(2u);}
        using var local=new MemoryStream();using(var writer=new BinaryWriter(local,Encoding.UTF8,true)){writer.Write("LOCALIZ_"u8);writer.Write(6u);writer.Write((uint)records.Length+12);writer.Write(1u);writer.Write(0x301u);writer.Write((byte)0);writer.Write("ENG"u8);writer.Write(records.ToArray());}
        WriteIndex(path,[(1016u,Container([(4091u,2u,(byte)0xA5)],local.ToArray())),(4091u,Container([(1033u,3u,(byte)0x11),(1031u,4u,(byte)0x12)])),(1033u,Dialogue(1)),(1031u,Container([]))]);
    }
    private static byte[] Container((uint Type,uint Id,byte Trailer)[] references,byte[]? tail=null)
    {using var bytes=new MemoryStream();using var writer=new BinaryWriter(bytes,Encoding.UTF8,true);writer.Write("COM_CONT"u8);writer.Write(6u);writer.Write((uint)(4+references.Length*9+(tail?.Length??0)));writer.Write((uint)references.Length);foreach(var reference in references){writer.Write(reference.Type);writer.Write(reference.Id);writer.Write(reference.Trailer);}if(tail is not null)writer.Write(tail);return bytes.ToArray();}
    private static byte[] Dialogue(int number)
    {
        var wem=new byte[98];"RIFF"u8.CopyTo(wem);BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(4),90);"WAVEfmt "u8.CopyTo(wem.AsSpan(8));BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(16),66);
        BinaryPrimitives.WriteUInt16LittleEndian(wem.AsSpan(20),0xFFFF);BinaryPrimitives.WriteUInt16LittleEndian(wem.AsSpan(22),2);BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(24),48000);BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(28),192000);BinaryPrimitives.WriteUInt16LittleEndian(wem.AsSpan(32),4);BinaryPrimitives.WriteUInt16LittleEndian(wem.AsSpan(34),16);BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(44),(uint)number*480);
        "data"u8.CopyTo(wem.AsSpan(86));BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(90),4);
        using var bytes=new MemoryStream();bytes.Write(new byte[49]);var prefix=bytes.GetBuffer();"QZIP\0CSNDDATA"u8.CopyTo(prefix);bytes.Position=49;using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true)){var name=Encoding.UTF8.GetBytes("Clip "+number+"_ENG");writer.Write((uint)name.Length);writer.Write(name);writer.Write((uint)wem.Length);writer.Write(wem);writer.Write(new byte[40]);}
        var result=bytes.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(17),(uint)result.Length-21);return result;
    }
    private static byte[] Localization(int count)
    {
        using var body=new MemoryStream();using(var writer=new BinaryWriter(body,Encoding.UTF8,true))foreach(var language in new[]{"ENG","FRE"}){writer.Write(0x301u);writer.Write((byte)0);writer.Write(Encoding.ASCII.GetBytes(language));for(var ordinal=0;ordinal<count;ordinal++){var key=Encoding.ASCII.GetBytes("Key "+(count-ordinal));var text=Encoding.Unicode.GetBytes(language+" text "+ordinal);writer.Write(1u);writer.Write((uint)key.Length);writer.Write(key);writer.Write((uint)text.Length);writer.Write(text);}}
        using var bytes=new MemoryStream();using var output=new BinaryWriter(bytes,Encoding.UTF8,true);output.Write("COM_CONT"u8);output.Write(6u);output.Write(4u);output.Write(0u);output.Write("LOCALIZ_"u8);output.Write(6u);output.Write((uint)body.Length+4);output.Write(2u);output.Write(body.ToArray());return bytes.ToArray();
    }
    public void Dispose()
    {
        try { VerifyPublishedAssemblies(); }
        finally { if(Directory.Exists(Root))Directory.Delete(Root,true); }
    }
    private static void VerifyPublishedAssemblies()
    {
        var package=Environment.GetEnvironmentVariable("DEVIANT_QA_PACKAGE");
        if(string.IsNullOrEmpty(package))return;
        using var manifest=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"package-manifest.json")));
        var hashes=manifest.RootElement.GetProperty("files").EnumerateArray().ToDictionary(entry=>entry.GetProperty("file").GetString()!,entry=>entry.GetProperty("sha256").GetString()!,StringComparer.OrdinalIgnoreCase);
        var loaded=AppDomain.CurrentDomain.GetAssemblies().Where(assembly=>assembly.GetName().Name!.StartsWith("Deviant.",StringComparison.Ordinal)&&assembly.GetName().Name!="Deviant.Desktop.Tests").Select(assembly=>new{File=Path.GetFileName(assembly.Location),Sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}).ToArray();
        foreach(var assembly in loaded)if(!hashes.TryGetValue(assembly.File,out var expected)||expected!=assembly.Sha256)throw new InvalidOperationException("Loaded assembly does not match package: "+assembly.File);
        var evidence=Environment.GetEnvironmentVariable("DEVIANT_QA_EVIDENCE");
        if(!string.IsNullOrEmpty(evidence)){Directory.CreateDirectory(evidence);File.WriteAllText(Path.Combine(evidence,"package-assemblies-"+Guid.NewGuid().ToString("N")+".json"),System.Text.Json.JsonSerializer.Serialize(loaded,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));}
    }
}
