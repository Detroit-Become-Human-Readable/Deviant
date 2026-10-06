using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public static class BufferInspections
{
    public static async Task<DecodeResult<BufferInspectionDocument>> InspectTextureAsync(ArchiveSnapshot snapshot,
        ResourceIdentity identity,TextureBufferPatternProfile profile,CancellationToken token=default)
    {
        if(identity.IndexContentId!=snapshot.IndexContentId||identity.MemberPath is not null||identity.Ordinal<0||identity.Ordinal>=snapshot.Records.Count)
            throw new ArgumentException("Select a resource from this archive.",nameof(identity));
        var source=await new TextureBufferPatternInspector().ReadAsync(snapshot,snapshot.Records[(int)identity.Ordinal],profile,cancellationToken:token);
        if(source.Value is not{}buffer)return new(null,source.Identity,source.Revision,source.Extents,source.Steps,source.Diagnostics,source.UnresolvedReferences,source.UnknownFields);
        var sections=new List<BufferInspectionSection>();
        void Add(string key,string label,BufferSectionKind kind,ReadOnlyMemory<byte> bytes,SourceExtent? physical=null,DecodedExtent? decoded=null)
        {token.ThrowIfCancellationRequested();sections.Add(new(key,label,kind,physical,decoded,Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(),bytes));}
        Add("header","Header",BufferSectionKind.Header,buffer.HeaderBytes,new(buffer.StoredExtent.RelativeSource,buffer.StoredExtent.Offset,buffer.HeaderBytes.Length));
        Add("decoded","Decoded buffer",BufferSectionKind.DecodedBuffer,buffer.Segments.DecodedPrefix,decoded:new(identity,0,buffer.Segments.DecodedPrefix.Length));
        foreach(var chunk in buffer.Segments.Chunks)
            Add("chunk/"+chunk.Ordinal,"Chunk "+(chunk.Ordinal+1),BufferSectionKind.DecodedChunk,chunk.DecodedBytes,chunk.PhysicalExtent,chunk.DecodedExtent);
        foreach(var gap in buffer.Segments.Gaps)
            Add("gap/"+gap.OrdinalBeforeChunk,"Gap "+(gap.OrdinalBeforeChunk+1),BufferSectionKind.Gap,gap.Bytes,gap.PhysicalExtent);
        if(buffer.Segments.RawSuffixExtent is{}suffix)
            Add("suffix","Suffix",BufferSectionKind.RawSuffix,buffer.Segments.RawSuffix,suffix);
        var fields=new Dictionary<string,string>(buffer.Assumptions,StringComparer.Ordinal)
        {
            ["headerHex"]=Convert.ToHexString(buffer.HeaderBytes),["lengthWordHex"]=Convert.ToHexString(buffer.RawLengthWord),
            ["skippedBytesHex"]=Convert.ToHexString(buffer.SkippedBytes),["payloadLength"]=buffer.CandidatePayloadLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["payloadSource"]=buffer.PayloadExtent.RelativeSource,["payloadOffset"]=buffer.PayloadExtent.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["payloadSha256"]=buffer.PayloadExtent.PayloadSha256??"",["cursorOriginEstablished"]=buffer.BoundaryBasis.CursorOriginEstablished.ToString()
        };
        var document=new BufferInspectionDocument("SEGS v"+buffer.Segments.Version,buffer.Profile.ToString(),"Observed ETF buffer pattern",buffer.StoredExtent,sections,[],fields);
        return new(document,source.Identity,source.Revision,source.Extents,source.Steps,source.Diagnostics,source.UnresolvedReferences,source.UnknownFields);
    }
}
