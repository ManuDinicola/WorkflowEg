using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Flowd.Contracts.Deployments;
using Flowd.Contracts.Execution;

namespace Flowd.Integration.Tests;

/// <summary>FR-UT-005: deploy a form, read the form of a task, and have the submission validated against it.</summary>
[Trait("Category", "Integration")]
public sealed class FormApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Form = """
        { "id": "approval", "components": [
          { "type": "number", "key": "amount", "validate": { "required": true, "min": 1 } },
          { "type": "textfield", "key": "reason", "validate": { "required": true } } ] }
        """;

    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="p" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:userTask id="U"><bpmn:extensionElements><flowd:formDefinition formKey="approval"/></bpmn:extensionElements></bpmn:userTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="U"/><bpmn:sequenceFlow id="f2" sourceRef="U" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<HttpResponseMessage> DeployFormAsync(TestApp app, string json)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Add(file, "resources", "approval.form");
        return await app.Client.PostAsync(new Uri("/v1/forms", UriKind.Relative), content);
    }

    [Fact]
    public async Task FR_UT_005_the_task_exposes_its_form_and_invalid_submissions_are_rejected_before_the_task_completes()
    {
        await using var app = await TestApp.StartAsync(postgres);
        (await DeployFormAsync(app, Form)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.DeployAsync(Process);
        var instance = await app.StartInstanceAsync("p");
        var task = (await app.Client.GetFromJsonAsync<PageDto<UserTaskDto>>(new Uri("/v1/user-tasks", UriKind.Relative)))!.Items.Single();

        var form = (await app.Client.GetFromJsonAsync<JsonObject>(new Uri($"/v1/user-tasks/{task.Key}/form", UriKind.Relative)))!;
        form["formId"]!.GetValue<string>().ShouldBe("approval");
        form["schema"]!["components"]!.AsArray().Count.ShouldBe(2);

        var rejected = await app.Client.PostAsJsonAsync(new Uri($"/v1/user-tasks/{task.Key}/complete", UriKind.Relative), new CompleteUserTaskRequestDto(JsonNode.Parse("""{"amount":0}""")!.AsObject(), "bob"));
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await rejected.Content.ReadAsStringAsync();
        body.ShouldContain("FORM_MIN");
        body.ShouldContain("FORM_REQUIRED");
        (await app.GetInstanceAsync(instance)).State.ShouldBe("ACTIVE");

        var accepted = await app.Client.PostAsJsonAsync(new Uri($"/v1/user-tasks/{task.Key}/complete", UriKind.Relative), new CompleteUserTaskRequestDto(JsonNode.Parse("""{"amount":5,"reason":"ok"}""")!.AsObject(), "bob"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.GetInstanceAsync(instance)).State.ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task FR_UT_005_form_versions_follow_the_content_and_malformed_forms_are_rejected()
    {
        await using var app = await TestApp.StartAsync(postgres);

        var first = JsonNode.Parse(await (await DeployFormAsync(app, Form)).Content.ReadAsStringAsync())!["forms"]![0]!;
        var same = JsonNode.Parse(await (await DeployFormAsync(app, Form)).Content.ReadAsStringAsync())!["forms"]![0]!;
        var changed = JsonNode.Parse(await (await DeployFormAsync(app, Form.Replace("\"min\": 1", "\"min\": 2", StringComparison.Ordinal))).Content.ReadAsStringAsync())!["forms"]![0]!;

        first["version"]!.GetValue<int>().ShouldBe(1);
        same["isNewVersion"]!.GetValue<bool>().ShouldBeFalse();
        changed["version"]!.GetValue<int>().ShouldBe(2);
        (await DeployFormAsync(app, "{ not json")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DeployFormAsync(app, """{"components":[]}""")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await app.Client.GetAsync(new Uri("/v1/forms/nope", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
