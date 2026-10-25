using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-UI-001..004: the built-in page and the analytics it shows.</summary>
[Trait("Category", "Integration")]
public sealed class UiAndAnalyticsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="p" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task FR_UI_001_the_page_is_served_without_a_key_even_when_authentication_is_on_and_the_root_redirects()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true }) { BaseAddress = app.Client.BaseAddress };

        var page = await noRedirect.GetAsync(new Uri("/ui", UriKind.Relative));
        var root = await noRedirect.GetAsync(new Uri("/", UriKind.Relative));

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await page.Content.ReadAsStringAsync()).ShouldContain("<title>Flowd</title>");
        root.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await noRedirect.GetAsync(new Uri("/v1/analytics/summary", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FR_UI_004_analytics_report_counts_durations_and_the_element_heat_map()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await app.DeployAsync(Process);
        await app.StartInstanceAsync("p");
        await app.StartInstanceAsync("p");
        await app.CompleteAsync((await app.ActivateAsync("work", maxJobs: 1)).Single().Key);

        var summary = (await app.Client.GetFromJsonAsync<JsonObject>(new Uri("/v1/analytics/summary", UriKind.Relative)))!;
        var process = (await app.Client.GetFromJsonAsync<JsonObject>(new Uri("/v1/analytics/processes/p", UriKind.Relative)))!;

        summary["instancesByState"]!["ACTIVE"]!.GetValue<int>().ShouldBe(1);
        summary["instancesByState"]!["COMPLETED"]!.GetValue<int>().ShouldBe(1);
        summary["jobsByState"]!.AsObject().Count.ShouldBeGreaterThan(0);
        process["averageDurationSeconds"]!.GetValue<double>().ShouldBeGreaterThanOrEqualTo(0);
        var task = process["elements"]!.AsArray().Single(e => e!["elementId"]!.GetValue<string>() == "A")!;
        task["activations"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task FR_UI_005_the_embedded_modeler_files_are_served_without_a_key_and_unknown_files_are_404()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: true);

        var script = await app.Client.GetAsync(new Uri("/ui/vendor/bpmn-modeler.production.min.js", UriKind.Relative));
        var css = await app.Client.GetAsync(new Uri("/ui/vendor/diagram-js.css", UriKind.Relative));

        script.StatusCode.ShouldBe(HttpStatusCode.OK);
        script.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        (await script.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(100_000);
        css.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.Client.GetAsync(new Uri("/ui/vendor/nope.js", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Client.GetAsync(new Uri("/ui/vendor/..%2Fappsettings.json", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await app.Client.GetAsync(new Uri("/ui", UriKind.Relative))).Content.ReadAsStringAsync()).ShouldContain("Modeler");
    }

    [Fact]
    public async Task FR_SEC_007_the_ui_offers_sign_in_only_when_an_oidc_client_is_configured()
    {
        await using var plain = await TestApp.StartAsync(postgres, secure: true);
        await using var configured = await TestApp.StartAsync(postgres, secure: true, "--Flowd:Auth:JwtAuthority=https://idp.example.com/", "--Flowd:Ui:Oidc:ClientId=flowd-ui");

        var none = JsonNode.Parse(await plain.Client.GetStringAsync(new Uri("/ui/config", UriKind.Relative)))!;
        var some = JsonNode.Parse(await configured.Client.GetStringAsync(new Uri("/ui/config", UriKind.Relative)))!;

        none["oidc"].ShouldBeNull();
        some["oidc"]!["authority"]!.GetValue<string>().ShouldBe("https://idp.example.com");
        some["oidc"]!["clientId"]!.GetValue<string>().ShouldBe("flowd-ui");
        some["oidc"]!["scopes"]!.GetValue<string>().ShouldBe("openid profile");
    }
}
