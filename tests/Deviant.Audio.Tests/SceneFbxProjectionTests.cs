using System.Collections;
using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class SceneFbxProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedGeometryKeepsDistinctPlacementsAndCentersThroughNodeTransforms(bool centered)
    {
        using var fixture = CreateSnapshot(firstWorld: Matrix4x4.CreateTranslation(10, 0, 0),
            secondWorld: Matrix4x4.CreateTranslation(14, 0, 0));
        var source = fixture.Meshes.Values.Single().Geometry.Primitives[0];
        var result = SceneFbxProjection.Build(fixture, new([new("instance-a", source.Key), new("instance-b", source.Key)],
            Placement: centered ? SceneExportPlacement.Centered : SceneExportPlacement.Scene) { PreserveInstances = true });
        var primitive = Assert.Single(result.Geometry.Primitives);
        Assert.Same(source.Positions, primitive.Positions);
        Assert.Same(source.Normals, primitive.Normals);
        Assert.Single(result.Parts);
        Assert.Equal(2, result.Geometry.Nodes.Count);
        Assert.All(result.Geometry.Nodes, node => Assert.Equal(new[] { primitive.Key }, node.PrimitiveKeys));
        var translations = result.Geometry.Nodes.Select(node => node.Transform!.Elements.Skip(12).Take(3).ToArray()).ToArray();
        Near(new(4, 0, 0), new Vector3(translations[1][0], translations[1][1], translations[1][2]) -
            new Vector3(translations[0][0], translations[0][1], translations[0][2]));
        if (centered) Near(new(-2.5f, -.5f, 0), new(translations[0][0], translations[0][1], translations[0][2]));
        else Near(new(10, 0, 0), new(translations[0][0], translations[0][1], translations[0][2]));
    }

    [Fact]
    public void SharedMirroredGeometryHasItsOwnWindingAndKeepsLocalAttributes()
    {
        using var fixture = CreateSnapshot(secondWorld: Matrix4x4.CreateScale(-2, 3, 1) * Matrix4x4.CreateTranslation(4, 5, 6));
        var result = SceneFbxProjection.Build(fixture, new([new("instance-a", "surface-b"), new("instance-b", "surface-b")])
            { PreserveInstances = true });
        Assert.Equal(2, result.Geometry.Primitives.Count);
        var mirrored = result.Parts.Single(part => part.Mirrored);
        var primitive = result.Geometry.Primitives.Single(part => part.Key == mirrored.OutputKey);
        Assert.Equal(new uint[] { 0, 2, 1 }, primitive.Indices);
        Assert.Same(mirrored.Source.Positions, primitive.Positions);
        Assert.Same(mirrored.Source.TextureCoordinates, primitive.TextureCoordinates);
        Assert.Equal(MatrixLayout.RowMajor, result.Geometry.Nodes[1].Transform!.Layout);
        Assert.Equal(VectorMultiplicationSide.Row, result.Geometry.Nodes[1].Transform!.VectorSide);
    }

    [Fact]
    public void ExactPrimitiveSelectionKeepsOnlyTheRequestedPartAndItsMaterialReferences()
    {
        using var fixture = CreateSnapshot();
        var source = fixture.Meshes.Values.Single().Geometry.Primitives[1];
        var result = SceneFbxProjection.Build(fixture, new([new("instance-a", source.Key)]));
        var primitive = Assert.Single(result.Geometry.Primitives);
        var part = Assert.Single(result.Parts);
        Assert.Same(source, part.Source);
        Assert.Equal("instance-a", part.Occurrence.Key);
        Assert.Equal(source.Positions, primitive.Positions);
        Assert.Equal(source.Indices, primitive.Indices);
        Assert.Equal(source.Submeshes[0].Materials, Assert.Single(primitive.Submeshes).Materials);
        Assert.NotEqual(source.Key, primitive.Key);
        Assert.Equal(primitive.Key, part.OutputKey);
        Assert.Equal(new[] { primitive.Key }, Assert.Single(result.Geometry.Nodes).PrimitiveKeys);
        Assert.Equal(1, result.BaseUvCount);
        Assert.False(result.Merged);
    }

    [Fact]
    public void RepeatedOccurrencesOfTheSameMeshRemainSeparateAndOverlappingSelectionsDoNotDuplicateParts()
    {
        using var fixture = CreateSnapshot(secondWorld: Matrix4x4.CreateTranslation(5, 7, 9));
        var result = SceneFbxProjection.Build(fixture, new([
            new("instance-a", "surface-a"), new("instance-a"), new("instance-a", "surface-a"),
            new("instance-b", "surface-a")
        ]));
        Assert.Equal(3, result.Geometry.Primitives.Count);
        Assert.Equal(3, result.Parts.Count);
        Assert.Equal(2, result.Geometry.Nodes.Count);
        Assert.Equal(3, result.Geometry.Primitives.Select(part => part.Key).Distinct().Count());
        var first = result.Parts.Single(part => part.Occurrence.Key == "instance-a" && part.Source.Key == "surface-a");
        var repeated = result.Parts.Single(part => part.Occurrence.Key == "instance-b");
        Assert.Equal(first.Mesh.Identity, repeated.Mesh.Identity);
        var firstPositions = result.Geometry.Primitives.Single(part => part.Key == first.OutputKey).Positions;
        var repeatedPositions = result.Geometry.Primitives.Single(part => part.Key == repeated.OutputKey).Positions;
        for (var vertex = 0; vertex < firstPositions.Count; vertex++)
            Near(firstPositions[vertex] + new Vector3(5, 7, 9), repeatedPositions[vertex]);
        Assert.Equal(new[] { 2, 1 }, result.Geometry.Nodes.Select(node => node.PrimitiveKeys.Count));
    }

    [Fact]
    public void NonuniformScaleUsesTheInverseTransposeForNormalsAndTheWorldBasisForTangents()
    {
        var normal = Vector3.Normalize(new Vector3(1, 1, 0));
        var tangent = Vector3.Normalize(new Vector3(1, -1, 0));
        var source = Triangle("sloped", [Vector3.Zero, Vector3.UnitZ, new(1, -1, 0)]) with
        {
            Normals = Enumerable.Repeat(normal, 3).ToArray(),
            Tangents = Enumerable.Repeat(new Vector4(tangent, -1), 3).ToArray()
        };
        using var fixture = CreateSnapshot([source], Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(7, 11, 13));
        var result = SceneFbxProjection.Build(fixture, new([new("instance-a")]));
        var projected = Assert.Single(result.Geometry.Primitives);
        Near(new(7, 11, 13), projected.Positions[0]);
        Near(new(7, 11, 17), projected.Positions[1]);
        Near(new(9, 8, 13), projected.Positions[2]);
        var expectedNormal = Vector3.Normalize(new Vector3(.5f, 1f / 3f, 0));
        var expectedTangent = Vector3.Normalize(new Vector3(2, -3, 0));
        Assert.All(projected.Normals!, value => Near(expectedNormal, value));
        Assert.All(projected.Tangents!, value =>
        {
            Near(expectedTangent, new(value.X, value.Y, value.Z));
            Assert.Equal(-1f, value.W);
        });
        var edgeOne = projected.Positions[1] - projected.Positions[0];
        var edgeTwo = projected.Positions[2] - projected.Positions[0];
        Assert.InRange(Math.Abs(Vector3.Dot(projected.Normals![0], edgeOne)), 0, 1e-5f);
        Assert.InRange(Math.Abs(Vector3.Dot(projected.Normals![0], edgeTwo)), 0, 1e-5f);
        Assert.InRange(Math.Abs(Vector3.Dot(projected.Normals![0], expectedTangent)), 0, 1e-5f);
        Near(Vector3.Zero, source.Positions[0]);
        Assert.Equal(normal, source.Normals![0]);
    }

    [Fact]
    public void MirroringReversesTriangleWindingAndTangentHandednessWithoutReorderingVertexAttributes()
    {
        var source = Triangle("triangle", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]) with
        {
            Colors = [new(1, 0, 0, 1), new(0, 1, 0, .5f), new(0, 0, 1, .25f)]
        };
        using var fixture = CreateSnapshot([source], Matrix4x4.CreateScale(-2, 3, 1) * Matrix4x4.CreateTranslation(4, 5, 6));
        var result = SceneFbxProjection.Build(fixture, new([new("instance-a")]));
        var projected = Assert.Single(result.Geometry.Primitives);
        Assert.True(Assert.Single(result.Parts).Mirrored);
        Assert.Equal(new uint[] { 0, 2, 1 }, projected.Indices);
        Near(new(4, 5, 6), projected.Positions[0]);
        Near(new(2, 5, 6), projected.Positions[1]);
        Near(new(4, 8, 6), projected.Positions[2]);
        Assert.All(projected.Normals!, value => Near(Vector3.UnitZ, value));
        Assert.All(projected.Tangents!, value => Assert.Equal(new Vector4(-1, 0, 0, 1), value));
        var faceNormal = Vector3.Normalize(Vector3.Cross(
            projected.Positions[(int)projected.Indices[1]] - projected.Positions[(int)projected.Indices[0]],
            projected.Positions[(int)projected.Indices[2]] - projected.Positions[(int)projected.Indices[0]]));
        Near(projected.Normals![0], faceNormal);
        Assert.Equal(source.TextureCoordinates![0], projected.TextureCoordinates![0]);
        Assert.Equal(source.Colors, projected.Colors);
        Assert.Equal(new uint[] { 0, 1, 2 }, source.Indices);
        Assert.Equal(-1f, source.Tangents![0].W);
    }

    [Fact]
    public void MergingRetainsCoincidentVerticesUvSeamsColorsAndEachMaterialRange()
    {
        using var fixture = CreateSnapshot();
        var sources = fixture.Meshes.Values.Single().Geometry.Primitives;
        var result = SceneFbxProjection.Build(fixture, new([
            new("instance-a", "surface-a"), new("instance-a", "surface-b"), new("instance-b", "surface-a")
        ], Merge: true));
        var merged = Assert.Single(result.Geometry.Primitives);
        Assert.True(result.Merged);
        Assert.Equal(IndexElementFormat.UInt32, merged.IndexFormat);
        Assert.Equal(15, merged.Positions.Count);
        Assert.Equal(Enumerable.Range(0, 15).Select(index => (uint)index), merged.Indices);
        Assert.Equal(new[] { 0, 6, 9 }, result.Parts.Select(part => part.FirstVertex));
        Assert.Equal(new[] { 0, 6, 9 }, result.Parts.Select(part => part.FirstIndex));
        Assert.All(result.Parts, part => Assert.Equal(merged.Key, part.OutputKey));
        Assert.Equal(new[] { 0, 3, 6, 9, 12 }, merged.Submeshes.Select(submesh => submesh.FirstIndex));
        Assert.All(merged.Submeshes, submesh => Assert.Equal(3, submesh.IndexCount));
        Assert.Equal(5, merged.Submeshes.Select(submesh => submesh.Key).Distinct().Count());
        var expectedMaterials = sources[0].Submeshes.Concat(sources[1].Submeshes).Concat(sources[0].Submeshes)
            .Select(submesh => submesh.Materials.Single()).ToArray();
        Assert.Equal(expectedMaterials, merged.Submeshes.Select(submesh => submesh.Materials.Single()));
        Assert.Equal(2, result.BaseUvCount);
        Assert.Equal(2, merged.TextureCoordinates!.Count);
        Assert.All(merged.TextureCoordinates!, set => Assert.Equal(15, set.Count));
        Assert.Equal(merged.Positions[0], merged.Positions[3]);
        Assert.NotEqual(merged.TextureCoordinates![0][0], merged.TextureCoordinates![0][3]);
        Assert.Equal(merged.Positions[0], merged.Positions[9]);
        Assert.Equal(sources[0].TextureCoordinates![0], merged.TextureCoordinates![0].Take(6));
        Assert.Equal(sources[1].TextureCoordinates![0], merged.TextureCoordinates![0].Skip(6).Take(3));
        Assert.Equal(sources[0].TextureCoordinates![1], merged.TextureCoordinates![1].Take(6));
        Assert.All(merged.TextureCoordinates![1].Skip(6).Take(3), uv => Assert.Equal(Vector2.Zero, uv));
        Assert.Equal(sources[0].Colors!, merged.Colors!.Take(6));
        Assert.All(merged.Colors!.Skip(6).Take(3), color => Assert.Equal(Vector4.One, color));
        Assert.Equal(sources[0].Colors!, merged.Colors!.Skip(9));
        Assert.Equal(15, merged.Normals!.Count);
        Assert.Equal(15, merged.Tangents!.Count);
        Assert.Equal(new[] { merged.Key }, Assert.Single(result.Geometry.Nodes).PrimitiveKeys);
        Assert.Equal(6, sources[0].Positions.Count);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, sources[0].Indices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CenteringUsesTheCompleteSelectionBoundsAndPreservesInstanceSpacing(bool merge)
    {
        using var fixture = CreateSnapshot(firstWorld: Matrix4x4.CreateTranslation(10, 20, 30),
            secondWorld: Matrix4x4.CreateTranslation(14, 22, 30));
        var selection = new RenderMeshSelection[] { new("instance-a", "surface-a"), new("instance-b", "surface-a") };
        var scene = SceneFbxProjection.Build(fixture, new(selection, Merge: merge));
        var centered = SceneFbxProjection.Build(fixture, new(selection, Merge: merge, Placement: SceneExportPlacement.Centered));
        var original = scene.Geometry.Primitives.SelectMany(primitive => primitive.Positions).ToArray();
        var moved = centered.Geometry.Primitives.SelectMany(primitive => primitive.Positions).ToArray();
        Assert.Equal(original.Length, moved.Length);
        for (var vertex = 0; vertex < original.Length; vertex++) Near(original[vertex] - new Vector3(12.5f, 21.5f, 30), moved[vertex]);
        Near(new(-2.5f, -1.5f, 0), moved[0]);
        Near(new(1.5f, .5f, 0), moved[6]);
        Near(new(4, 2, 0), moved[6] - moved[0]);
        Near(new(-2.5f, -1.5f, 0), moved.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min));
        Near(new(2.5f, 1.5f, 0), moved.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max));
        foreach (var primitive in centered.Geometry.Primitives)
        {
            Near(primitive.Positions.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min), primitive.Bounds!.Minimum);
            Near(primitive.Positions.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max), primitive.Bounds!.Maximum);
        }
    }

    [Fact]
    public void MergedMirroredOccurrenceAppliesItsOwnWindingAfterTheVertexOffset()
    {
        var triangle = Triangle("triangle", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]);
        using var fixture = CreateSnapshot([triangle], secondWorld: Matrix4x4.CreateScale(-1, 1, 1));
        var result = SceneFbxProjection.Build(fixture, new([new("instance-a"), new("instance-b")], Merge: true));
        var merged = Assert.Single(result.Geometry.Primitives);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 5, 4 }, merged.Indices);
        Assert.Equal(new[] { 0, 3 }, result.Parts.Select(part => part.FirstVertex));
        Assert.False(result.Parts[0].Mirrored);
        Assert.True(result.Parts[1].Mirrored);
        Assert.All(merged.Tangents!.Take(3), tangent => Assert.Equal(-1f, tangent.W));
        Assert.All(merged.Tangents!.Skip(3), tangent => Assert.Equal(1f, tangent.W));
        Assert.Equal(triangle.Submeshes[0].Materials, merged.Submeshes[1].Materials);
        Assert.Equal(3, merged.Submeshes[1].FirstIndex);
    }

    [Fact]
    public void HiddenOccurrencesAreIncludedOnlyWhenRequested()
    {
        using var fixture = CreateSnapshot(secondVisible: false);
        var selection = new RenderMeshSelection[] { new("instance-a", "surface-a"), new("instance-b", "surface-a") };
        var visible = SceneFbxProjection.Build(fixture, new(selection));
        Assert.Equal("instance-a", Assert.Single(visible.Parts).Occurrence.Key);
        var all = SceneFbxProjection.Build(fixture, new(selection, IncludeHidden: true));
        Assert.Equal(new[] { "instance-a", "instance-b" }, all.Parts.Select(part => part.Occurrence.Key));
        Assert.False(all.Parts[1].Occurrence.SourceVisible);
        Assert.Throws<InvalidDataException>(() => SceneFbxProjection.Build(fixture, new([new("instance-b")])));
    }

    [Fact]
    public void CancelledProjectionDoesNotPublishAResultOrChangeSourceGeometry()
    {
        using var fixture = CreateSnapshot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = fixture.Meshes.Values.Single().Geometry.Primitives[0];
        var positions = source.Positions.ToArray();
        Assert.ThrowsAny<OperationCanceledException>(() => SceneFbxProjection.Build(fixture, new([new("instance-a")]), cancellation.Token));
        Assert.Equal(positions, source.Positions);
    }

    [Fact]
    public void CancellationDuringGeometryEnumerationStopsBeforeMergedOutputIsReturned()
    {
        using var cancellation = new CancellationTokenSource();
        var positions = new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var source = Triangle("triangle", positions) with
        {
            Positions = new CancellationList<Vector3>(positions, cancellation)
        };
        using var fixture = CreateSnapshot([source]);
        Assert.ThrowsAny<OperationCanceledException>(() => SceneFbxProjection.Build(fixture,
            new([new("instance-a")], Merge: true), cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, positions);
    }

    [Theory]
    [InlineData("missing-instance", null)]
    [InlineData("instance-a", "missing-part")]
    public void StaleOccurrenceAndPrimitiveSelectionsAreRejected(string occurrence, string? primitive)
    {
        using var fixture = CreateSnapshot();
        Assert.Throws<InvalidDataException>(() => SceneFbxProjection.Build(fixture, new([new(occurrence, primitive)])));
    }

    [Fact]
    public void SingularAndNonfiniteTransformsAreRejected()
    {
        using var singular = CreateSnapshot(firstWorld: Matrix4x4.CreateScale(1, 0, 1));
        Assert.Throws<InvalidDataException>(() => SceneFbxProjection.Build(singular, new([new("instance-a")])));
        var invalid = Matrix4x4.Identity;
        invalid.M41 = float.NaN;
        using var nonfinite = CreateSnapshot(firstWorld: invalid);
        Assert.Throws<InvalidDataException>(() => SceneFbxProjection.Build(nonfinite, new([new("instance-a")])));
    }

    [Fact]
    public void EmptySelectionAndUnsupportedTopologyAreRejected()
    {
        using var fixture = CreateSnapshot();
        Assert.Throws<InvalidDataException>(() => SceneFbxProjection.Build(fixture, new([])));
        var line = Triangle("line", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]) with { Topology = PrimitiveTopology.Lines };
        using var unsupported = CreateSnapshot([line]);
        Assert.Throws<InvalidDataException>(() => SceneFbxProjection.Build(unsupported, new([new("instance-a")])));
    }

    private static RenderAssetSnapshot CreateSnapshot(IReadOnlyList<MeshPrimitive>? primitives = null,
        Matrix4x4? firstWorld = null, Matrix4x4? secondWorld = null, bool secondVisible = true)
    {
        var identity = new ResourceIdentity("generated-scene-index", 1);
        var profile = new CoordinateProfile("generated-basis", "1",
            new(CoordinateAxis.PositiveX, CoordinateAxis.PositiveY, CoordinateAxis.PositiveZ),
            CoordinateHandedness.Right, 1, MatrixLayout.RowMajor, VectorMultiplicationSide.Row,
            Evidence: new(true, "generated-geometry", "Synthetic coordinate basis"));
        var geometry = new GeometryAsset(profile, [], primitives ?? CreatePrimitives(), new Dictionary<string, string>());
        var mesh = new RenderMesh(identity, 0, geometry);
        var bounds = new GeometryBounds(new(-100), new(100));
        RenderOccurrence Occurrence(string key, long ordinal, Matrix4x4 world, bool visible) =>
            new(key, new(identity.IndexContentId, ordinal), new(identity.IndexContentId, ordinal + 10),
                "Generated instance", mesh.Key, world, bounds, []) { SourceVisible = visible };
        return new(new(identity.IndexContentId, "generated-revision"), new Dictionary<string, RenderMesh> { [mesh.Key] = mesh },
            [Occurrence("instance-a", 2, firstWorld ?? Matrix4x4.Identity, true),
                Occurrence("instance-b", 3, secondWorld ?? Matrix4x4.Identity, secondVisible)],
            new Dictionary<ResourceIdentity, RenderMaterial>(), bounds, [], [], new(2, 2, 0, 0, 0, 0), [], new AssetMemoryBudget());
    }

    private static IReadOnlyList<MeshPrimitive> CreatePrimitives()
    {
        var first = Triangle("surface-a", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]) with
        {
            Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Indices = [0, 1, 2, 3, 4, 5],
            Submeshes = [new("material-a", 0, 3, [Material("100")]), new("material-b", 3, 3, [Material("101")])],
            Normals = Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            Tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 6).ToArray(),
            TextureCoordinates = [
                new Vector2[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, new(.5f, .5f), new(.75f, .5f), new(.5f, .75f) },
                new Vector2[] { new(.1f, .2f), new(.3f, .4f), new(.5f, .6f), new(.7f, .8f), new(.9f, .1f), new(.2f, .3f) }
            ],
            Colors = [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 0, .5f), new(0, 1, 1, .5f), new(1, 0, 1, .5f)]
        };
        var second = Triangle("surface-b", [new(2, 0, 0), new(3, 0, 0), new(2, 1, 0)]) with
        {
            Submeshes = [new("material-c", 0, 3, [Material("102")])]
        };
        return [first, second];
    }

    private static MeshPrimitive Triangle(string key, IReadOnlyList<Vector3> positions) =>
        new(key, PrimitiveTopology.Triangles, positions, [0, 1, 2], IndexElementFormat.UInt16,
            [new(key + "/material", 0, 3, [Material("100")])],
            Enumerable.Repeat(Vector3.UnitZ, positions.Count).ToArray(),
            Enumerable.Repeat(new Vector4(1, 0, 0, -1), positions.Count).ToArray(),
            [new Vector2[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY }]);

    private static ResourceReference Material(string id) => new(IdentifierNamespace.Native, id, ResourceType: 2133);

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-5f);

    private sealed class CancellationList<T>(IReadOnlyList<T> items, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator()
        {
            cancellation.Cancel();
            foreach (var item in items) yield return item;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
