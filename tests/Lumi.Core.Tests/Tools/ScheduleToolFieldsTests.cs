using Lumi.Core.Tools;

namespace Lumi.Core.Tests.Tools;

public class ScheduleToolFieldsTests
{
    [Fact]
    public void Granted_permissions_is_removed_and_other_fields_are_kept()
    {
        var json = ScheduleToolFields.RemoveGrantedPermissions("""{"name":"早報","granted_permissions":["write"]}""");

        var obj = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.False(obj.ContainsKey("granted_permissions"));
        Assert.Equal("早報", obj["name"]!.GetValue<string>());
    }

    [Fact]
    public void Json_without_the_field_is_unchanged_in_meaning()
    {
        var json = ScheduleToolFields.RemoveGrantedPermissions("""{"name":"早報"}""");

        Assert.Equal("""{"name":"早報"}""", json);
    }

    [Fact]
    public void Non_object_json_is_returned_as_is()
    {
        Assert.Equal("[1,2]", ScheduleToolFields.RemoveGrantedPermissions("[1,2]"));
        Assert.Equal("not json", ScheduleToolFields.RemoveGrantedPermissions("not json"));
    }

    [Fact]
    public void Always_ask_tools_cannot_be_set_to_always_allow()
    {
        var ask = new ToolDescriptor("create", "建立", "測試用。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5), AlwaysAsk: true);
        var write = new ToolDescriptor("write", "寫入", "測試用。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5));

        Assert.False(IToolCatalog.CanAlwaysAllow(ask));
        Assert.True(IToolCatalog.CanAlwaysAllow(write));
    }
}
