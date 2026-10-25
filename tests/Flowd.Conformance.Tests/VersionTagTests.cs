using Flowd.Conformance.Tests.Support;
using static Flowd.Conformance.Tests.Support.Bpmn;

namespace Flowd.Conformance.Tests;

/// <summary>FR-DEP-007: version tags on process definitions and call activities that bind to a tag.</summary>
public sealed class VersionTagTests
{
    private static string Child(string jobType, string? tag = null)
    {
        var xml = Process("child", Start(), Service("Work", jobType), End(), Flow("f1", "Start", "Work"), Flow("f2", "Work", "End"));
        return tag is null ? xml : xml.Replace("<bpmn:process id=\"child\" isExecutable=\"true\">", $"<bpmn:process id=\"child\" isExecutable=\"true\"><bpmn:extensionElements><flowd:versionTag value=\"{tag}\"/></bpmn:extensionElements>", StringComparison.Ordinal);
    }

    private static string Parent(string calledElementFlags) =>
        Process("parent", Start(), CallActivity("Call", "child", calledElementFlags), End(), Flow("f1", "Start", "Call"), Flow("f2", "Call", "End"));

    [Fact]
    public async Task FR_DEP_007_a_call_activity_bound_to_a_version_tag_starts_the_newest_version_with_that_tag()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Child("old-work", tag: "stable"));
        await engine.DeployAsync(Child("new-work")); // newer version, no tag
        await engine.DeployAsync(Parent("<flowd:calledElement bindingType=\"versionTag\" versionTag=\"stable\"/>"));

        await engine.StartAsync("parent");

        (await engine.ActivateJobAsync("old-work")).ShouldNotBeNull();
        (await engine.ActivateJobAsync("new-work")).ShouldBeNull();
    }

    [Fact]
    public async Task FR_DEP_007_without_a_binding_the_latest_version_is_called_and_an_unknown_tag_raises_an_incident()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Child("old-work", tag: "stable"));
        await engine.DeployAsync(Child("new-work"));
        await engine.DeployAsync(Parent(string.Empty));
        await engine.StartAsync("parent");
        (await engine.ActivateJobAsync("new-work")).ShouldNotBeNull();

        var tagged = new TestEngine();
        await tagged.DeployAsync(Child("old-work", tag: "stable"));
        await tagged.DeployAsync(Parent("<flowd:calledElement bindingType=\"versionTag\" versionTag=\"nope\"/>"));
        var key = await tagged.StartAsync("parent");

        tagged.State(key).Incidents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FR_EXEC_011_the_called_process_id_can_be_an_expression_over_the_variables()
    {
        var engine = new TestEngine();
        await engine.DeployAsync(Child("child-work"));
        await engine.DeployAsync(Process("parent", Start(), CallActivity("Call", "= target"), End(), Flow("f1", "Start", "Call"), Flow("f2", "Call", "End")));

        await engine.StartAsync("parent", new System.Text.Json.Nodes.JsonObject { ["target"] = "child" });

        (await engine.ActivateJobAsync("child-work")).ShouldNotBeNull();
    }
}
