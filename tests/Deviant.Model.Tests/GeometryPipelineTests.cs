using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;
using Deviant.Formats.Models;
using SharpGLTF.Schema2;
using GeometryPrimitive = Deviant.Assets.MeshPrimitive;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class GeometryPipelineTests
{
    [Fact]
    public void ValidatorFindsNonFinitePositionsAndOutOfRangeIndices()
    {
        var primitive = Primitive() with
        {
            Positions = [new(0, 0, 0), new(float.NaN, 1, 0), new(0, 0, 1)],
            Indices = [0, 1, 3]
        };

        var report = GeometryValidator.Validate(Asset(primitive));

        Assert.False(report.IsValid);
        Assert.Contains(report.Diagnostics, item => item.Code == "PositionNonFinite");
        Assert.Contains(report.Diagnostics, item => item.Code == "IndexOutOfRange");
    }

    [Fact]
    public void ValidatorRejectsSubmeshRangesThatSplitTriangleGroups()
    {
        var primitive = Primitive() with
        {
            Submeshes = [new GeometrySubmesh("partial", 0, 2, [])]
        };

        var report = GeometryValidator.Validate(Asset(primitive));

        Assert.False(report.IsValid);
        Assert.Contains(report.Diagnostics, item => item.Code == "SubmeshTopologyRangeInvalid");
    }

    [Fact]
    public async Task ExporterWritesReadableGlbAndReportsUnresolvedMaterialValues()
    {
        var primitive = Primitive() with
        {
            Submeshes = [new GeometrySubmesh("part", 0, 3,
                [new ResourceReference(IdentifierNamespace.Archive, "mat-42", ResourceType: 11)])]
        };
        var asset = Asset(primitive);
        var identity = new ResourceIdentity("synthetic-index", 7);
        var revision = new SourceRevision(identity.IndexContentId, "synthetic-source");
        var decoded = new DecodeResult<GeometryAsset>(asset, identity, revision, [], [], [], [],
            new Dictionary<string, string>());
        var root = Path.Combine(Path.GetTempPath(), "deviant-model-export-test-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        var output = Path.Combine(root, "output.glb");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var report = await new GltfGeometryExporter().ExportAsync(decoded, sourceRoot, output);

            Assert.True(File.Exists(output));
            Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 4));
            var model = ModelRoot.Load(output);
            Assert.Single(model.LogicalMeshes);
            Assert.Single(model.LogicalNodes);
            Assert.Equal(identity, report.Identity);
            Assert.Equal(revision, report.Revision);
            var materialLoss = Assert.Single(report.Losses, item => item.Code == "MaterialValuesUnavailable");
            Assert.Equal("mat-42", Assert.Single(materialLoss.References!).Id);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExporterRejectsUnknownCoordinateBasis()
    {
        var asset = Asset(Primitive()) with
        {
            Coordinates = new CoordinateProfile("unknown", "1", Handedness: CoordinateHandedness.Unknown,
                MetersPerUnit: null, Evidence: new RelationshipEvidence(false, "synthetic", "not verified"))
        };
        var identity = new ResourceIdentity("synthetic-index", 7);
        var decoded = new DecodeResult<GeometryAsset>(asset, identity,
            new SourceRevision(identity.IndexContentId, "synthetic-source"), [], [], [], [],
            new Dictionary<string, string>());
        var root = Path.Combine(Path.GetTempPath(), "deviant-model-export-reject-test-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var error = await Assert.ThrowsAsync<GeometryExportException>(() =>
                new GltfGeometryExporter().ExportAsync(decoded, sourceRoot, Path.Combine(root, "output.glb")));
            Assert.Equal("CoordinateProfileIncomplete", error.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static GeometryAsset Asset(GeometryPrimitive primitive) => new(
        new CoordinateProfile("synthetic-right-handed", "1",
            new AxisMapping(CoordinateAxis.PositiveX, CoordinateAxis.PositiveY, CoordinateAxis.PositiveZ),
            CoordinateHandedness.Right, 1, Evidence: new RelationshipEvidence(true, "synthetic-test", "generic test basis")),
        [new GeometryNode("node", null,
            new ResourceReference(IdentifierNamespace.Archive, "node"), [primitive.Key])],
        [primitive], new Dictionary<string, string>());

    private static GeometryPrimitive Primitive() => new("triangle", PrimitiveTopology.Triangles,
        [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)], [0, 1, 2],
        IndexElementFormat.UInt16, [new GeometrySubmesh("default", 0, 3, [])]);
}
