using System.Numerics;
using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Model.Tests;

public sealed partial class FbxGeometryExporterTests
{
    [Fact]
    public async Task WritesSelectedTriangleDetailWithHierarchyAttributesAndMaterialAssignment()
    {
        var asset = CreateAsset();
        var decoded = CreateDecoded(asset);
        var selection = new FbxGeometrySelection(decoded.Identity, "mesh-detail-key", LodIndex: 2, GroupIndex: 4);
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "source"));
        var output = Path.Combine(root, "model.fbx");
        try
        {
            var report = await new FbxGeometryExporter().ExportAsync(
                new FbxModelExportInput(decoded, selection, Reserve), Path.Combine(root, "source"), output);
            var text = await File.ReadAllTextAsync(output);

            Assert.Contains("FBXVersion: 7400", text);
            Assert.Contains("DeviantCoordinateProfile", text);
            Assert.Contains("P: \"CoordAxis\",\"int\",\"Integer\",\"\",0", text);
            Assert.Contains("P: \"UpAxis\",\"int\",\"Integer\",\"\",1", text);
            Assert.Contains("P: \"FrontAxis\",\"int\",\"Integer\",\"\",2", text);
            Assert.Contains("P: \"FrontAxisSign\",\"int\",\"Integer\",\"\",1", text);
            Assert.Contains("LayerElementNormal", text);
            Assert.Contains("LayerElementTangent", text);
            Assert.Contains("TangentsW", text);
            Assert.Contains("LayerElementColor", text);
            Assert.Contains("UVSet0", text);
            Assert.Contains("UVSet1", text);
            var layerZeroStart = text.IndexOf("Layer: 0", StringComparison.Ordinal);
            var layerOneStart = text.IndexOf("Layer: 1", StringComparison.Ordinal);
            Assert.True(layerZeroStart >= 0 && layerOneStart > layerZeroStart);
            var primaryLayer = text[layerZeroStart..layerOneStart];
            var secondaryLayer = text[layerOneStart..text.IndexOf("Properties70:", layerOneStart, StringComparison.Ordinal)];
            Assert.Contains("Type: \"LayerElementTangent\" TypedIndex: 0", primaryLayer);
            Assert.Contains("Type: \"LayerElementUV\" TypedIndex: 0", primaryLayer);
            Assert.DoesNotContain("Type: \"LayerElementUV\" TypedIndex: 1", primaryLayer);
            Assert.Contains("Type: \"LayerElementUV\" TypedIndex: 1", secondaryLayer);
            Assert.DoesNotContain("LayerElementTangent", secondaryLayer);
            Assert.Contains("LayerElementMaterial", text);
            Assert.Contains("DeviantSourceNodeKey", text);
            Assert.Equal(selection, report.Selection);
            Assert.Equal(1, report.MeshCount);
            Assert.Equal(3, report.NodeCount);
            Assert.Equal(1, report.MaterialCount);
            Assert.Equal(64, report.OutputSha256.Length);
            Assert.Contains(report.Losses, loss => loss.Code == "MaterialValuesUnavailable");
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task WritesLeftHandedAndPermutedAxisDirectionsWithoutMirroringMeshData()
    {
        var root = NewRoot();
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var leftHanded = CreateAsset() with
            {
                Coordinates = CreateProfile() with
                {
                    Id = "generated-left-handed",
                    Axes = new AxisMapping(CoordinateAxis.PositiveX, CoordinateAxis.PositiveY, CoordinateAxis.NegativeZ),
                    Handedness = CoordinateHandedness.Left
                }
            };
            var leftDecoded = CreateDecoded(leftHanded);
            var leftText = await ExportTextAsync(leftDecoded, sourceRoot, Path.Combine(root, "left-handed.fbx"));
            Assert.Contains("P: \"FrontAxis\",\"int\",\"Integer\",\"\",2", leftText);
            Assert.Contains("P: \"FrontAxisSign\",\"int\",\"Integer\",\"\",-1", leftText);
            Assert.Matches(@"Vertices: \*9 \{ a:\s*0,0,0,1,0,0,0,1,0", leftText);

            var zUp = CreateAsset() with
            {
                Coordinates = CreateProfile() with
                {
                    Id = "generated-right-handed-z-up",
                    Axes = new AxisMapping(CoordinateAxis.PositiveX, CoordinateAxis.PositiveZ, CoordinateAxis.NegativeY)
                }
            };
            var zUpText = await ExportTextAsync(CreateDecoded(zUp), sourceRoot, Path.Combine(root, "z-up.fbx"));
            Assert.Contains("P: \"CoordAxis\",\"int\",\"Integer\",\"\",0", zUpText);
            Assert.Contains("P: \"UpAxis\",\"int\",\"Integer\",\"\",2", zUpText);
            Assert.Contains("P: \"UpAxisSign\",\"int\",\"Integer\",\"\",1", zUpText);
            Assert.Contains("P: \"FrontAxis\",\"int\",\"Integer\",\"\",1", zUpText);
            Assert.Contains("P: \"FrontAxisSign\",\"int\",\"Integer\",\"\",-1", zUpText);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task WritesBoundedBinaryFbxWithEmbeddedPngsAndTypedGeometry()
    {
        var root = NewRoot();
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var asset = CreateAsset();
        var decoded = CreateDecoded(asset);
        var materialIdentity = new ResourceIdentity("generated-material-index", 8);
        var materialRevision = new SourceRevision(materialIdentity.IndexContentId, "generated-material-revision");
        var materialReference = Ref("material-source");
        var bindings = CreateBindings();
        var material = new MaterialAsset(materialIdentity, materialRevision, "generated-material-profile", bindings,
            bindings.Select(binding => binding.Texture).ToArray(), []);
        var materialInput = new FbxMaterialExportInput(material,
            [new(materialReference, new(ReferenceStatus.Unique, [materialIdentity]))],
            bindings.Select(CreateTextureInput).ToArray(), _ => Task.CompletedTask);
        var output = Path.Combine(root, "model.fbx");
        try
        {
            var report = await new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-generated-detail", 0, 0), Reserve, [materialInput]),
                sourceRoot, output, new(MediaPackaging: FbxMediaPackaging.Embedded));
            var bytes = await File.ReadAllBytesAsync(output);
            var document = ParseBinary7400(bytes);

            Assert.Equal(FbxMediaPackaging.Embedded, report.MediaPackaging);
            Assert.Equal("Kaydara FBX Binary  \0\x1a\0", Encoding.ASCII.GetString(bytes, 0, 23));
            Assert.Equal(7400u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(23, 4)));
            Assert.Equal(1, document.Roots.Count(node => node.Name == "FileId"));
            var creationTime = ReadString(document.Roots.Single(node => node.Name == "CreationTime").Properties.Single());
            Assert.Equal(ExpectedBinaryMagic(creationTime, footer: false),
                document.Roots.Single(node => node.Name == "FileId").Properties.Single().Payload);
            Assert.Equal(ExpectedBinaryMagic(creationTime, footer: true), document.FooterMagic);
            Assert.Equal(2, document.Roots.Single(node => node.Name == "Objects").Children.Count(node => node.Name == "Video"));
            Assert.Equal(1, document.Roots.Single(node => node.Name == "Objects").Children.Count(node => node.Name == "Geometry"));
            Assert.Equal(3, document.Roots.Single(node => node.Name == "Objects").Children.Count(node => node.Name == "Model"));
            Assert.All(document.Roots.Single(node => node.Name == "Objects").Children, node =>
                Assert.EndsWith("\0\u0001" + node.Name, ReadString(node.Properties[1])));

            var geometry = document.Roots.Single(node => node.Name == "Objects").Children.Single(node => node.Name == "Geometry");
            var positionArray = geometry.Children.Single(node => node.Name == "Vertices").Properties.Single();
            Assert.Equal((byte)'d', positionArray.TypeCode);
            Assert.Equal(asset.Primitives[0].Positions.SelectMany(position => new[] { (double)position.X, position.Y, position.Z }),
                ReadDoubles(positionArray));
            var primaryLayer = geometry.Children.First(node => node.Name == "Layer");
            Assert.Contains(primaryLayer.Children, node => node.Name == "LayerElement" &&
                ReadString(node.Children.Single(child => child.Name == "Type").Properties.Single()) == "LayerElementTangent" &&
                BinaryPrimitives.ReadInt32LittleEndian(node.Children.Single(child => child.Name == "TypedIndex").Properties.Single().Payload) == 0);

            var contentProperties = document.Roots.Single(node => node.Name == "Objects").Children
                .Where(node => node.Name == "Video")
                .Select(node => node.Children.Single(child => child.Name == "Content").Properties.Single()).ToArray();
            Assert.All(contentProperties, property =>
            {
                Assert.Equal((byte)'R', property.TypeCode);
                Assert.True(property.Payload.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                var hash = Convert.ToHexString(SHA256.HashData(property.Payload)).ToLowerInvariant();
                Assert.Contains(report.MaterialTextures!, texture => texture.Sha256 == hash);
            });
            Assert.Equal(4, document.Roots.Single(node => node.Name == "Connections").Children
                .Count(node => node.Name == "C" && node.Properties[0].TypeCode == (byte)'S' &&
                    Encoding.UTF8.GetString(node.Properties[0].Payload) == "OP"));

            var texture = document.Roots.Single(node => node.Name == "Objects").Children
                .First(node => node.Name == "Texture");
            var textureProperties = texture.Children.Single(node => node.Name == "Properties70").Children;
            var scaling = textureProperties.Single(node => ReadString(node.Properties[0]) == "Scaling");
            Assert.Equal(2d, ReadDouble(scaling.Properties[4]));
            Assert.Equal(1d / 0.75d, ReadDouble(scaling.Properties[5]), 6);
            var translation = textureProperties.Single(node => ReadString(node.Properties[0]) == "Translation");
            Assert.Equal(-0.2d, ReadDouble(translation.Properties[4]), 6);
            Assert.Equal(-0.2d / 0.75d, ReadDouble(translation.Properties[5]), 6);
            Assert.False(Directory.Exists(output + ".assets"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task FailedEmbeddedWritePreservesPriorFbxAndPublishesNoSidecars()
    {
        var root = NewRoot();
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var output = Path.Combine(root, "model.fbx");
        var previous = Encoding.UTF8.GetBytes("prior-user-output");
        await File.WriteAllBytesAsync(output, previous);
        try
        {
            var decoded = CreateDecoded(CreateAsset());
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-generated-detail", 0, 0), Reserve),
                sourceRoot, output, new(Overwrite: true, MediaPackaging: FbxMediaPackaging.Embedded, MaximumBinaryBytes: 1)));
            Assert.Contains("file-size limit", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(previous, await File.ReadAllBytesAsync(output));
            Assert.False(Directory.Exists(output + ".assets"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(root, "*.media-stage-*", SearchOption.AllDirectories));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CancellationBeforeEmbeddedCommitPreservesPriorFbxAndCleansTemporaryFiles()
    {
        var root = NewRoot();
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var output = Path.Combine(root, "model.fbx");
        var previous = Encoding.UTF8.GetBytes("prior-user-output");
        await File.WriteAllBytesAsync(output, previous);
        using var cancellation = new CancellationTokenSource();
        var validationCalls = 0;
        Task ValidateGeometry(CancellationToken _)
        {
            if (Interlocked.Increment(ref validationCalls) == 2) cancellation.Cancel();
            return Task.CompletedTask;
        }
        try
        {
            var decoded = CreateDecoded(CreateAsset());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-generated-detail", 0, 0), Reserve,
                    ValidateGeometrySourceRevision: ValidateGeometry),
                sourceRoot, output, new(Overwrite: true, MediaPackaging: FbxMediaPackaging.Embedded), cancellationToken: cancellation.Token));
            Assert.Equal(previous, await File.ReadAllBytesAsync(output));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(root, "*.media-stage-*", SearchOption.AllDirectories));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task WritesMaterialRolesUvTransformsAndContentAddressedTextureSidecars()
    {
        var root = NewRoot();
        var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var asset = CreateAsset(); var decoded = CreateDecoded(asset);
        var materialIdentity = new ResourceIdentity("generated-material-index", 8);
        var materialRevision = new SourceRevision(materialIdentity.IndexContentId, "generated-material-revision");
        var materialReference = Ref("material-source");
        var bindings = CreateBindings();
        var textures = bindings.Select(CreateTextureInput).ToArray();
        var graph = new MaterialSurfaceGraph(
            new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4,
                [new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv1)],
                TextureSlot: 0), null, null, [MaterialInput.Uv1], [0], ["generic graph preserved as approximation"]);
        var material = new MaterialAsset(materialIdentity, materialRevision, "generated-material-profile", bindings,
            bindings.Select(binding => binding.Texture).ToArray(), ["generated diagnostic"]) { SurfaceGraph = graph };
        var materialInput = new FbxMaterialExportInput(material,
            [new(materialReference, new(ReferenceStatus.Unique, [materialIdentity]))], textures,
            _ => Task.CompletedTask);
        var budget = new ReservationCounter();
        var output = Path.Combine(root, "model.fbx");
        try
        {
            var report = await new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-generated-detail", 0, 0), budget.Reserve, [materialInput]),
                sourceRoot, output);
            var text = await File.ReadAllTextAsync(output);
            var reports = Assert.IsAssignableFrom<IReadOnlyList<FbxMaterialTextureExportResult>>(report.MaterialTextures);

            Assert.Equal(4, reports.Count);
            Assert.Equal(2, reports.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("TextureName: \"DiffuseColor\"", text);
            Assert.Contains("TextureName: \"NormalMap\"", text);
            Assert.Contains("TextureName: \"DeviantOpacity\"", text);
            Assert.Contains("TextureName: \"DeviantRoughness\"", text);
            Assert.Contains("ModelUVScaling: 0.5,0.75", text);
            Assert.Contains("ModelUVTranslation: 0.1,0.2", text);
            Assert.Contains("P: \"UVSet\",\"KString\",\"\",\"\",\"UVSet1\"", text);
            Assert.Contains("C: \"OP\"", text);
            Assert.DoesNotContain(root, text);
            Assert.Contains(report.Losses, loss => loss.Code == "MaterialGraphApproximated");
            Assert.Equal(2, report.Losses.Count(loss => loss.Code == "MaterialRoleCustomChannel"));
            Assert.Equal(0, budget.ActiveReservations);
            Assert.True(budget.PeakReservations >= 1);

            foreach (var media in reports)
            {
                var sidecar = Path.Combine(root, media.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(sidecar));
                Assert.Equal(media.Length, new FileInfo(sidecar).Length);
                Assert.Equal(media.Sha256, (await HashFileAsync(sidecar)));
                Assert.Equal(new TextureSubresourceSelection(0, null, 0), media.SelectedSubresource);
                Assert.InRange(media.TextureCoordinateSet, 0, 1);
                Assert.Equal(media.Role == MaterialTextureRole.BaseColor, media.SourceIsSrgb);
                Assert.Equal(media.SourceIsSrgb, media.OutputIsSrgb);
                Assert.False(media.SourceIsHdr);
                Assert.False(media.OutputIsHdr);
                Assert.Equal(64, media.SourcePayloadSha256.Length);
            }
            var textureFiles = Directory.GetFiles(Path.Combine(root, "model.fbx.assets", "textures"), "*.png");
            Assert.Equal(2, textureFiles.Length);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task SingularUvMappingPreservesExistingOutputBeforeMediaPublication()
    {
        var root = NewRoot();
        var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var asset = CreateAsset(); var decoded = CreateDecoded(asset);
        var identity = new ResourceIdentity("generated-material-index", 8);
        var binding = CreateBindings()[0] with { UvScale = new Vector2(0, 1) };
        var material = new MaterialAsset(identity, new(identity.IndexContentId, "generated-revision"),
            "generated", [binding], [binding.Texture], []);
        var materialInput = new FbxMaterialExportInput(material,
            [new(Ref("material-source"), new(ReferenceStatus.Unique, [identity]))],
            [CreateTextureInput(binding)], _ => Task.CompletedTask);
        var budget = new ReservationCounter(); var output = Path.Combine(root, "model.fbx");
        try
        {
            await File.WriteAllTextAsync(output, "existing export");
            var error = await Assert.ThrowsAsync<FbxGeometryExportException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "generated-detail"), budget.Reserve, [materialInput]),
                sourceRoot, output, new(Overwrite: true)));
            Assert.Equal("MaterialUvTransformSingular", error.Code);
            Assert.Equal("existing export", await File.ReadAllTextAsync(output));
            Assert.False(Directory.Exists(output + ".assets"));
            Assert.Equal(0, budget.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task GeometrySourceValidationFailurePreservesOutputAndRollsBackNewMedia(int failAt, bool cancel)
    {
        var root = NewRoot(); var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var asset = CreateAsset(); var decoded = CreateDecoded(asset);
        var identity = new ResourceIdentity("generated-material-index", 8);
        var bindings = CreateBindings();
        var material = new MaterialAsset(identity, new(identity.IndexContentId, "generated-revision"),
            "generated", bindings, bindings.Select(b => b.Texture).ToArray(), []);
        var materialInput = new FbxMaterialExportInput(material,
            [new(Ref("material-source"), new(ReferenceStatus.Unique, [identity]))],
            bindings.Select(CreateTextureInput).ToArray(), _ => Task.CompletedTask);
        var budget = new ReservationCounter(); var output = Path.Combine(root, "model.fbx"); var calls = 0;
        Task ValidateGeometry(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++calls != failAt) return Task.CompletedTask;
            return cancel ? Task.FromCanceled(new CancellationToken(true)) :
                Task.FromException(new ArchiveException("SourceChanged", "Generated geometry source changed"));
        }
        try
        {
            await File.WriteAllTextAsync(output, "existing geometry export");
            var input = new FbxModelExportInput(decoded, new(decoded.Identity, "generated-detail"), budget.Reserve,
                [materialInput], ValidateGeometry);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FbxGeometryExporter().ExportAsync(input,
                sourceRoot, output, new(Overwrite: true)));
            else
            {
                var error = await Assert.ThrowsAsync<ArchiveException>(() => new FbxGeometryExporter().ExportAsync(input,
                    sourceRoot, output, new(Overwrite: true)));
                Assert.Equal("SourceChanged", error.Code);
            }
            Assert.Equal(failAt, calls);
            Assert.Equal("existing geometry export", await File.ReadAllTextAsync(output));
            var mediaDirectory = Path.Combine(output + ".assets", "textures");
            Assert.True(!Directory.Exists(mediaDirectory) || Directory.GetFiles(mediaDirectory, "*.png").Length == 0);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            Assert.Equal(0, budget.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task AmbiguousMaterialJoinKeepsNeutralPlaceholderWithoutPublishingMedia()
    {
        var root = NewRoot(); var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var decoded = CreateDecoded(CreateAsset());
        var identity = new ResourceIdentity("generated-material-index", 9);
        var material = new MaterialAsset(identity, new(identity.IndexContentId, "generated-material-revision"),
            "generated-profile", [], [], []);
        var input = new FbxMaterialExportInput(material,
            [new(Ref("material-source"), new(ReferenceStatus.Ambiguous, [identity, new("other-index", 10)]))],
            [], _ => Task.CompletedTask);
        var output = Path.Combine(root, "ambiguous.fbx");
        try
        {
            var report = await new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-detail"), Reserve, [input]), sourceRoot, output);
            var text = await File.ReadAllTextAsync(output);
            Assert.Contains(report.Losses, loss => loss.Code == "MaterialReferenceAmbiguous");
            Assert.DoesNotContain("Texture:", text);
            Assert.DoesNotContain("Video:", text);
            Assert.Empty(report.MaterialTextures!);
            Assert.False(Directory.Exists(Path.Combine(root, "ambiguous.fbx.assets")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ContradictoryTextureSurfaceIdentityFailsBeforeOutputChanges()
    {
        var root = NewRoot(); var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var output = Path.Combine(root, "model.fbx");
        var oldBytes = Encoding.UTF8.GetBytes("previous fbx"); await File.WriteAllBytesAsync(output, oldBytes);
        try
        {
            var decoded = CreateDecoded(CreateAsset());
            var materialIdentity = new ResourceIdentity("generated-material-index", 2);
            var materialRevision = new SourceRevision(materialIdentity.IndexContentId, "material-revision");
            var binding = CreateBindings()[0];
            var inputTexture = CreateTextureInput(binding);
            var wrongSurfaceDocument = MakeTextureDocument(new ResourceIdentity("different-texture-index", 3), 28);
            var wrongSurface = new DdsTextureDecoder().DecodeMip(wrongSurfaceDocument);
            var material = new MaterialAsset(materialIdentity, materialRevision, "generated-profile", [binding], [binding.Texture], []);
            var export = new FbxMaterialExportInput(material,
                [new(Ref("material-source"), new(ReferenceStatus.Unique, [materialIdentity]))],
                [inputTexture with { Surface = wrongSurface }], _ => Task.CompletedTask);
            var error = await Assert.ThrowsAsync<FbxGeometryExportException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new(decoded, new(decoded.Identity, "selected-detail"), Reserve, [export]),
                    sourceRoot, output, new(Overwrite: true)));
            Assert.Equal("TextureSurfaceIdentityMismatch", error.Code);
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(output));
            Assert.False(Directory.Exists(Path.Combine(root, "model.fbx.assets")));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HdrMaterialTexturePreservesDdsOrReportsTheChosenPngTransform(bool preserveHdr)
    {
        var root = NewRoot(); var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        try
        {
            var decoded = CreateDecoded(CreateAsset());
            var materialIdentity = new ResourceIdentity("generated-hdr-material-index", 3);
            var materialRevision = new SourceRevision(materialIdentity.IndexContentId, "material-revision");
            var binding = CreateBindings()[0];
            var textureIdentity = new ResourceIdentity("generated-hdr-texture-index", 5);
            var textureRevision = new SourceRevision(textureIdentity.IndexContentId, "texture-revision");
            var hdrBytes = new byte[164];
            "DDS "u8.CopyTo(hdrBytes); PutUInt32(hdrBytes, 4, 124); PutUInt32(hdrBytes, 8, 0x0000_1007);
            PutUInt32(hdrBytes, 12, 4); PutUInt32(hdrBytes, 16, 4);
            PutUInt32(hdrBytes, 76, 32); PutUInt32(hdrBytes, 80, 4); "DX10"u8.CopyTo(hdrBytes.AsSpan(84));
            PutUInt32(hdrBytes, 108, 0x0000_1000); PutUInt32(hdrBytes, 128, 95);
            PutUInt32(hdrBytes, 132, 3); PutUInt32(hdrBytes, 140, 1);
            var document = new DdsTextureReader().Read(hdrBytes, textureIdentity, textureRevision);
            var hdr = new DdsTextureDecoder().DecodeMip(document);
            var surface = preserveHdr ? hdr : HdrDisplayTransform.ToSdr(hdr, -2, TextureToneMapOperator.Aces);
            var textureInput = new FbxMaterialTextureInput(binding.SlotOrdinal, binding.Texture,
                new(ReferenceStatus.Unique, [textureIdentity]), document, surface, textureRevision, _ => Task.CompletedTask);
            var material = new MaterialAsset(materialIdentity, materialRevision, "generated-hdr-profile", [binding], [binding.Texture], []);
            var materialInput = new FbxMaterialExportInput(material,
                [new(Ref("material-source"), new(ReferenceStatus.Unique, [materialIdentity]))], [textureInput], _ => Task.CompletedTask);
            var output = Path.Combine(root, "hdr.fbx");
            var report = await new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-detail"), Reserve, [materialInput]), sourceRoot, output);
            var media = Assert.Single(report.MaterialTextures!);

            Assert.True(media.SourceIsHdr);
            Assert.Equal(preserveHdr, media.OutputIsHdr);
            if (preserveHdr)
            {
                Assert.EndsWith(".dds", media.RelativePath);
                Assert.Equal(document.ContentSha256, media.Sha256);
                Assert.Equal(hdrBytes, await File.ReadAllBytesAsync(Path.Combine(root, media.RelativePath)));
                Assert.Null(media.DisplayTransform);
                Assert.DoesNotContain(report.Losses, loss => loss.Code == "TextureEncodingLoss");
                Assert.DoesNotContain(report.Losses, loss => loss.Code == "TextureColorSpaceRoleMismatch");
                var fbx = await File.ReadAllTextAsync(output);
                Assert.Contains("DeviantTextureEncodedSha256", fbx);
                Assert.Contains("WholeDocument", fbx);
                Assert.DoesNotContain("DeviantTexturePngSha256", fbx);
                return;
            }
            Assert.True(media.OutputIsSrgb);
            Assert.Equal(TextureToneMapOperator.Aces, media.DisplayTransform?.Operator);
            Assert.Equal(-2, media.DisplayTransform?.ExposureStops);
            Assert.Equal(new TextureSubresourceSelection(0, null, 0), media.SelectedSubresource);
            Assert.Contains(report.Losses, loss => loss.Code == "TextureEncodingLoss" && loss.Detail.Contains("Aces", StringComparison.Ordinal));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task FailedFinalSourceCheckCleansOnlyNewSidecarsAndPreservesPriorFbx()
    {
        var root = NewRoot(); var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var output = Path.Combine(root, "model.fbx");
        var oldBytes = Encoding.UTF8.GetBytes("previous fbx"); await File.WriteAllBytesAsync(output, oldBytes);
        var budget = new ReservationCounter();
        try
        {
            var decoded = CreateDecoded(CreateAsset());
            var materialIdentity = new ResourceIdentity("generated-material-index", 6);
            var materialRevision = new SourceRevision(materialIdentity.IndexContentId, "material-revision");
            var binding = CreateBindings()[0]; var texture = CreateTextureInput(binding);
            var material = new MaterialAsset(materialIdentity, materialRevision, "generated-profile", [binding], [binding.Texture], []);
            var export = new FbxMaterialExportInput(material,
                [new(Ref("material-source"), new(ReferenceStatus.Unique, [materialIdentity]))], [texture],
                _ => Task.FromException(new ArchiveException("SourceChanged", "generated revision changed")));
            var error = await Assert.ThrowsAsync<ArchiveException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-detail"), budget.Reserve, [export]),
                sourceRoot, output, new(Overwrite: true)));
            Assert.Equal("SourceChanged", error.Code);
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(output));
            var mediaRoot = Path.Combine(root, "model.fbx.assets", "textures");
            if (Directory.Exists(mediaRoot)) Assert.Empty(Directory.GetFiles(mediaRoot, "*.png"));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(0, budget.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task FailedOverwritePreservesPreviouslyCommittedContentAddressedMedia()
    {
        var root = NewRoot(); var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
        var output = Path.Combine(root, "model.fbx");
        try
        {
            var decoded = CreateDecoded(CreateAsset());
            var materialIdentity = new ResourceIdentity("generated-material-index", 7);
            var materialRevision = new SourceRevision(materialIdentity.IndexContentId, "material-revision");
            var binding = CreateBindings()[0];
            var texture = CreateTextureInput(binding);
            var material = new MaterialAsset(materialIdentity, materialRevision, "generated-profile", [binding], [binding.Texture], []);
            var joins = new[] { new FbxMaterialReferenceJoin(Ref("material-source"), new(ReferenceStatus.Unique, [materialIdentity])) };
            var first = new FbxMaterialExportInput(material, joins, [texture], _ => Task.CompletedTask);
            var firstReport = await new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-detail"), Reserve, [first]), sourceRoot, output);
            var mediaReport = Assert.Single(firstReport.MaterialTextures!);
            var sidecar = Path.Combine(root, mediaReport.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var originalMedia = await File.ReadAllBytesAsync(sidecar);
            var priorFbx = Encoding.UTF8.GetBytes("previous committed fbx");
            await File.WriteAllBytesAsync(output, priorFbx);

            var failing = new FbxMaterialExportInput(material, joins, [texture],
                _ => Task.FromException(new ArchiveException("SourceChanged", "generated revision changed")));
            var error = await Assert.ThrowsAsync<ArchiveException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected-detail"), Reserve, [failing]),
                sourceRoot, output, new(Overwrite: true)));

            Assert.Equal("SourceChanged", error.Code);
            Assert.Equal(priorFbx, await File.ReadAllBytesAsync(output));
            Assert.Equal(originalMedia, await File.ReadAllBytesAsync(sidecar));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task RejectedGeometryAndCancellationPreserveExistingDestination()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "source"));
        var output = Path.Combine(root, "model.fbx");
        var original = Encoding.UTF8.GetBytes("previous output");
        await File.WriteAllBytesAsync(output, original);
        try
        {
            var bad = CreateAsset() with
            {
                Primitives = [CreatePrimitive() with { Indices = [0, 1, 7] }]
            };
            var invalid = await Assert.ThrowsAsync<FbxGeometryExportException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new FbxModelExportInput(CreateDecoded(bad), new(CreateDecoded(bad).Identity, "selected-detail"), Reserve),
                    Path.Combine(root, "source"), output, new(Overwrite: true)));
            Assert.Equal("GeometryValidationFailed", invalid.Code);
            Assert.Equal(original, await File.ReadAllBytesAsync(output));

            var malformedColors = CreateAsset() with
            {
                Primitives = [CreatePrimitive() with { Colors = [new Vector4(1, 0, 0, 1)] }]
            };
            var colorError = await Assert.ThrowsAsync<FbxGeometryExportException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new FbxModelExportInput(CreateDecoded(malformedColors), new(CreateDecoded(malformedColors).Identity, "selected-detail"), Reserve),
                    Path.Combine(root, "source"), output, new(Overwrite: true)));
            Assert.Contains(colorError.Losses, loss => loss.Code == "ColorCountMismatch");
            Assert.Equal(original, await File.ReadAllBytesAsync(output));

            using var cts = new CancellationTokenSource();
            IDisposable CancelAtReservation(long bytes, string category)
            {
                Assert.True(bytes > 0);
                Assert.False(string.IsNullOrWhiteSpace(category));
                cts.Cancel();
                return NoopReservation.Instance;
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new FbxModelExportInput(CreateDecoded(CreateAsset()), new(CreateDecoded(CreateAsset()).Identity, "selected-detail"), CancelAtReservation),
                    Path.Combine(root, "source"), output, new(Overwrite: true), cts.Token));
            Assert.Equal(original, await File.ReadAllBytesAsync(output));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task RejectsUnverifiedCoordinateProfileAndInvalidSelectedDetail()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "source"));
        try
        {
            var unverified = CreateAsset() with
            {
                Coordinates = CreateProfile() with { Evidence = new RelationshipEvidence(false, "generated", "unverified") }
            };
            var coordinateError = await Assert.ThrowsAsync<FbxGeometryExportException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new FbxModelExportInput(CreateDecoded(unverified), new(CreateDecoded(unverified).Identity, "selected-detail"), Reserve),
                    Path.Combine(root, "source"), Path.Combine(root, "unverified.fbx")));
            Assert.Equal("CoordinateProfileUnverified", coordinateError.Code);

            var handednessMismatch = CreateAsset() with
            {
                Coordinates = CreateProfile() with { Handedness = CoordinateHandedness.Left }
            };
            var handednessError = await Assert.ThrowsAsync<FbxGeometryExportException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new FbxModelExportInput(CreateDecoded(handednessMismatch),
                        new(CreateDecoded(handednessMismatch).Identity, "selected-detail"), Reserve),
                    Path.Combine(root, "source"), Path.Combine(root, "mismatched-handedness.fbx")));
            Assert.Equal("CoordinateHandednessMismatch", handednessError.Code);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new FbxModelExportInput(CreateDecoded(CreateAsset()), new(CreateDecoded(CreateAsset()).Identity, "selected-detail", LodIndex: -1), Reserve),
                    Path.Combine(root, "source"), Path.Combine(root, "invalid-lod.fbx")));
            Assert.False(File.Exists(Path.Combine(root, "invalid-lod.fbx")));

            var decoded = CreateDecoded(CreateAsset());
            var identityMismatch = await Assert.ThrowsAsync<ArgumentException>(() =>
                new FbxGeometryExporter().ExportAsync(
                    new FbxModelExportInput(decoded,
                        new(new ResourceIdentity("another-index", 99), "selected-detail", LodIndex: 0), Reserve),
                    Path.Combine(root, "source"), Path.Combine(root, "mismatched-detail.fbx")));
            Assert.Contains("identity must match", identityMismatch.Message);
        }
        finally { DeleteRoot(root); }
    }

    private static ParsedBinaryDocument ParseBinary7400(byte[] bytes)
    {
        Assert.True(bytes.Length >= 27 + 13 + 16 + 128 + 16);
        Assert.Equal("Kaydara FBX Binary  \0\x1a\0", Encoding.ASCII.GetString(bytes, 0, 23));
        Assert.Equal(7400u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(23, 4)));
        var cursor = 27;
        var roots = new List<ParsedBinaryNode>();
        while (!IsNullRecord(bytes, cursor)) roots.Add(ParseNode(bytes, ref cursor));
        cursor += 13;
        Assert.True(cursor + 16 + 128 + 16 <= bytes.Length);
        cursor += 16; // Timestamp-derived footer magic.
        var alignment = 16 - cursor % 16;
        for (var index = 0; index < alignment; index++)
        {
            Assert.Equal((byte)0, bytes[cursor]);
            cursor++;
        }
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)));
        Assert.Equal(7400u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4, 4)));
        Assert.All(bytes.AsSpan(cursor + 8, 120).ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(new byte[] { 0xf8,0x5a,0x8c,0x6a,0xde,0xf5,0xd9,0x7e,0xec,0xe9,0x0c,0xe3,0x75,0x8f,0x29,0x0b },
            bytes.AsSpan(bytes.Length - 16).ToArray());
        var footerMagic = bytes.AsSpan(cursor - alignment - 16, 16).ToArray();
        Assert.Equal(cursor + 128 + 16, bytes.Length);
        return new(roots, footerMagic);
    }

    private static ParsedBinaryNode ParseNode(byte[] bytes, ref int cursor)
    {
        var start = cursor;
        var end = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)));
        var propertyCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4, 4)));
        var propertyBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 8, 4)));
        var nameLength = bytes[cursor + 12];
        cursor += 13;
        var name = Encoding.UTF8.GetString(bytes, cursor, nameLength);
        cursor += nameLength;
        var propertyEnd = checked(cursor + propertyBytes);
        var properties = new List<ParsedBinaryProperty>(propertyCount);
        for (var index = 0; index < propertyCount; index++)
        {
            var type = bytes[cursor++];
            switch (type)
            {
                case (byte)'I': case (byte)'F':
                    properties.Add(new(type, bytes.AsSpan(cursor, 4).ToArray(), null)); cursor += 4; break;
                case (byte)'L': case (byte)'D':
                    properties.Add(new(type, bytes.AsSpan(cursor, 8).ToArray(), null)); cursor += 8; break;
                case (byte)'C': case (byte)'B': case (byte)'Z':
                    properties.Add(new(type, [bytes[cursor++]], null)); break;
                case (byte)'Y':
                    properties.Add(new(type, bytes.AsSpan(cursor, 2).ToArray(), null)); cursor += 2; break;
                case (byte)'S': case (byte)'R':
                {
                    var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)));
                    cursor += 4;
                    properties.Add(new(type, bytes.AsSpan(cursor, length).ToArray(), null)); cursor += length;
                    break;
                }
                case (byte)'c': case (byte)'b': case (byte)'i': case (byte)'l': case (byte)'f': case (byte)'d':
                {
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4));
                    var encoding = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4, 4));
                    var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 8, 4)));
                    Assert.Equal(0u, encoding);
                    cursor += 12;
                    properties.Add(new(type, bytes.AsSpan(cursor, length).ToArray(), count)); cursor += length;
                    break;
                }
                default: throw new InvalidDataException($"Unexpected generated FBX property type {type}.");
            }
        }
        Assert.Equal(propertyEnd, cursor);

        var children = new List<ParsedBinaryNode>();
        while (cursor < end && !IsNullRecord(bytes, cursor)) children.Add(ParseNode(bytes, ref cursor));
        Assert.True(IsNullRecord(bytes, cursor));
        cursor += 13;
        Assert.Equal(end, cursor);
        Assert.True(cursor > start);
        return new(name, properties, children);
    }

    private static bool IsNullRecord(byte[] bytes, int offset) =>
        offset >= 0 && offset + 13 <= bytes.Length && bytes.AsSpan(offset, 13).IndexOfAnyExcept((byte)0) < 0;

    private static string ReadString(ParsedBinaryProperty property)
    {
        Assert.Equal((byte)'S', property.TypeCode);
        return Encoding.UTF8.GetString(property.Payload);
    }

    private static double ReadDouble(ParsedBinaryProperty property)
    {
        Assert.Equal((byte)'D', property.TypeCode);
        return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(property.Payload));
    }

    private static double[] ReadDoubles(ParsedBinaryProperty property)
    {
        Assert.Equal((byte)'d', property.TypeCode);
        Assert.Equal((uint)(property.Payload.Length / sizeof(double)), property.Count!.Value);
        return Enumerable.Range(0, property.Payload.Length / sizeof(double))
            .Select(index => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(
                property.Payload.AsSpan(index * sizeof(double), sizeof(double)))))
            .ToArray();
    }

    private static byte[] ExpectedBinaryMagic(string creationTime, bool footer)
    {
        byte[] order = [0x11,0x12,0x05,0x06,0x0b,0x0c,0x08,0x09,0x14,0x15,0x00,0x01,0x02,0x03,0x0e,0x0f];
        byte[] key = footer
            ? [0xfa,0xbc,0x9b,0x09,0xd0,0xc9,0xe4,0x67,0xb1,0x76,0xca,0x82,0x1d,0xff,0x19,0x78]
            : [0x18,0xb3,0x1a,0xea,0x86,0x24,0xfc,0xc3,0x8e,0xc9,0x80,0x23,0x97,0x25,0xc2,0xff];
        var bytes = Encoding.ASCII.GetBytes(creationTime);
        var magic = order.Select(index => bytes[index]).ToArray();
        if (footer)
        {
            for (var index = 2; index < magic.Length; index++) magic[index] ^= magic[index - 2];
            for (var index = 0; index < magic.Length; index++) magic[index] ^= bytes[order[index]];
        }
        for (var index = 1; index < magic.Length; index++) magic[index] ^= magic[index - 1];
        for (var index = 0; index < magic.Length; index++) magic[index] ^= key[index];
        return magic;
    }

    private sealed record ParsedBinaryDocument(IReadOnlyList<ParsedBinaryNode> Roots, byte[] FooterMagic);
    private sealed record ParsedBinaryNode(string Name, IReadOnlyList<ParsedBinaryProperty> Properties,
        IReadOnlyList<ParsedBinaryNode> Children);
    private sealed record ParsedBinaryProperty(byte TypeCode, byte[] Payload, uint? Count);

    private static GeometryAsset CreateAsset()
    {
        var primitive = CreatePrimitive();
        var parent = Matrix4x4.CreateScale(-2, 3, 1.5f) * Matrix4x4.CreateRotationY(0.3f) *
            Matrix4x4.CreateTranslation(4, -2, 7);
        var child = Matrix4x4.CreateScale(0.75f, 1.25f, 2) * Matrix4x4.CreateRotationZ(-0.2f) *
            Matrix4x4.CreateTranslation(1, 2, -3);
        return new(CreateProfile(),
            [
                new GeometryNode("parent", null, Ref("source-parent"), [], ToTransform(parent)),
                new GeometryNode("child", "parent", Ref("source-child"), [primitive.Key], ToTransform(child))
            ], [primitive], new Dictionary<string, string>());
    }

    private static MeshPrimitive CreatePrimitive() => new("mesh", PrimitiveTopology.Triangles,
        [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], IndexElementFormat.UInt32,
        [new GeometrySubmesh("surface", 0, 3, [Ref("material-source")])],
        [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)],
        [new(1, 0, 0, -1), new(1, 0, 0, -1), new(1, 0, 0, -1)],
        [[new(0, 0), new(1, 0), new(0, 1)], [new(0.1f, 0.2f), new(0.8f, 0.2f), new(0.1f, 0.9f)]],
        Colors: [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1)]);

    private static CoordinateProfile CreateProfile() => new("generated-test-basis", "1",
        new AxisMapping(CoordinateAxis.PositiveX, CoordinateAxis.PositiveY, CoordinateAxis.PositiveZ),
        CoordinateHandedness.Right, 1, MatrixLayout.RowMajor, VectorMultiplicationSide.Row,
        Evidence: new RelationshipEvidence(true, "generated-test", "synthetic, finite right-handed test basis"));

    private static SourceTransform ToTransform(Matrix4x4 m) => new(
        [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44],
        MatrixLayout.RowMajor, VectorMultiplicationSide.Row);

    private static DecodeResult<GeometryAsset> CreateDecoded(GeometryAsset asset)
    {
        var identity = new ResourceIdentity("generated-index", 1);
        return new(asset, identity, new(identity.IndexContentId, "generated-revision"), [], [], [], [],
            new Dictionary<string, string>());
    }

    private static async Task<string> ExportTextAsync(DecodeResult<GeometryAsset> decoded, string sourceRoot, string output)
    {
        var selection = new FbxGeometrySelection(decoded.Identity, "selected-generated-detail", LodIndex: 0);
        await new FbxGeometryExporter().ExportAsync(new(decoded, selection, Reserve), sourceRoot, output);
        return await File.ReadAllTextAsync(output);
    }

    private static ResourceReference Ref(string id) => new(IdentifierNamespace.Archive, id, ResourceType: 11);
    private static MaterialTextureBinding[] CreateBindings() =>
    [
        new(0, TextureRef(0), MaterialTextureRole.BaseColor, 1, new(0.5f, 0.75f), new(0.1f, 0.2f), Evidence()),
        new(1, TextureRef(1), MaterialTextureRole.Normal, 0, Vector2.One, Vector2.Zero, Evidence()),
        new(2, TextureRef(2), MaterialTextureRole.Opacity, 0, Vector2.One, Vector2.Zero, Evidence()),
        new(3, TextureRef(3), MaterialTextureRole.Roughness, 1, Vector2.One, Vector2.Zero, Evidence())
    ];

    private static ResourceReference TextureRef(int slot) =>
        new(IdentifierNamespace.Native, $"generated-texture-{slot}", ResourceType: 19);

    private static RelationshipEvidence Evidence() =>
        new(true, "generated-test", "synthetic typed material-to-texture relationship");

    private static FbxMaterialTextureInput CreateTextureInput(MaterialTextureBinding binding)
    {
        var textureIdentity = new ResourceIdentity($"generated-texture-index-{binding.SlotOrdinal}", binding.SlotOrdinal + 1);
        var textureRevision = new SourceRevision(textureIdentity.IndexContentId, $"generated-texture-revision-{binding.SlotOrdinal}");
        var document = MakeTextureDocument(textureIdentity, binding.Role == MaterialTextureRole.BaseColor ? 29u : 28u, textureRevision);
        var surface = new DdsTextureDecoder().DecodeMip(document);
        return new(binding.SlotOrdinal, binding.Texture, new(ReferenceStatus.Unique, [textureIdentity]),
            document, surface, textureRevision, _ => Task.CompletedTask);
    }

    private static DdsTextureDocument MakeTextureDocument(ResourceIdentity identity, uint dxgiFormat,
        SourceRevision? revision = null)
    {
        var bytes = new byte[152];
        "DDS "u8.CopyTo(bytes);
        PutUInt32(bytes, 4, 124); PutUInt32(bytes, 8, 0x0000_1007);
        PutUInt32(bytes, 12, 1); PutUInt32(bytes, 16, 1);
        PutUInt32(bytes, 76, 32); PutUInt32(bytes, 80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84));
        PutUInt32(bytes, 108, 0x0000_1000); PutUInt32(bytes, 128, dxgiFormat);
        PutUInt32(bytes, 132, 3); PutUInt32(bytes, 140, 1);
        new byte[] { 80, 120, 160, 255 }.CopyTo(bytes, 148);
        revision ??= new(identity.IndexContentId, "generated-texture-revision");
        return new DdsTextureReader().Read(bytes, identity, revision);
    }

    private static void PutUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)), value);

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

    private static IDisposable Reserve(long bytes, string category)
    { Assert.True(bytes > 0); return NoopReservation.Instance; }
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "deviant-fbx-test-" + Guid.NewGuid().ToString("N"));
    private static void DeleteRoot(string root) { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    private sealed class NoopReservation : IDisposable
    {
        public static readonly NoopReservation Instance = new();
        public void Dispose() { }
    }

    private sealed class ReservationCounter
    {
        private int active;
        private int peak;
        public int ActiveReservations => Volatile.Read(ref active);
        public int PeakReservations => Volatile.Read(ref peak);
        public IDisposable Reserve(long bytes, string category)
        {
            Assert.True(bytes > 0); Assert.False(string.IsNullOrWhiteSpace(category));
            var current = Interlocked.Increment(ref active);
            int old;
            while (current > (old = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, current, old) != old) { }
            return new Reservation(this);
        }
        private void Release() => Interlocked.Decrement(ref active);
        private sealed class Reservation(ReservationCounter owner) : IDisposable
        {
            private int disposed;
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(); }
        }
    }
}
