using System.Text.Json.Nodes;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.Tools;

public class ToolSnapshotJsonTests
{
    private static ToolDefinition Def(string name) =>
        new(name, $"說明 {name}", JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string"}},"additionalProperties":false}""")!);

    [Fact]
    public void RoundTrips_NamesDescriptionsAndSchemas()
    {
        var json = ToolSnapshotJson.Serialize([Def("a"), Def("b")]);

        var back = ToolSnapshotJson.Deserialize(json);

        Assert.NotNull(back);
        Assert.Equal(["a", "b"], back!.Select(t => t.Name));
        Assert.Equal("說明 a", back[0].Description);
        Assert.Equal(Def("a").InputSchema.ToJsonString(), back[0].InputSchema.ToJsonString());
    }

    [Fact]
    public void EmptyList_RoundTripsAsEmptyNotNull()
    {
        Assert.Empty(ToolSnapshotJson.Deserialize(ToolSnapshotJson.Serialize([]))!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{\"name\":\"a\"}]")]
    public void MissingOrMalformed_ReturnsNull(string? json)
    {
        Assert.Null(ToolSnapshotJson.Deserialize(json));
    }
}
