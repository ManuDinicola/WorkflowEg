using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-DMN-003: deploy DMN files and evaluate decisions through the API.</summary>
[Trait("Category", "Integration")]
public sealed class DecisionApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Dmn = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">
          <decision id="shipping" name="Shipping">
            <decisionTable hitPolicy="FIRST">
              <input><inputExpression><text>weight</text></inputExpression></input>
              <output name="cost"/>
              <rule><inputEntry><text>&lt; 5</text></inputEntry><outputEntry><text>5</text></outputEntry></rule>
              <rule><inputEntry><text>-</text></inputEntry><outputEntry><text>weight * 2</text></outputEntry></rule>
            </decisionTable>
          </decision>
        </definitions>
        """;

    private static async Task<HttpResponseMessage> DeployAsync(TestApp app, string xml)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
        form.Add(file, "resources", "shipping.dmn");
        return await app.Client.PostAsync(new Uri("/v1/decisions", UriKind.Relative), form);
    }

    private static async Task<JsonNode?> EvaluateAsync(TestApp app, string json)
    {
        var response = await app.Client.PostAsync(new Uri("/v1/decisions/shipping/evaluate", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"];
    }

    [Fact]
    public async Task FR_DMN_003_a_deployed_decision_is_evaluated_with_the_given_variables()
    {
        await using var app = await TestApp.StartAsync(postgres);
        (await DeployAsync(app, Dmn)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EvaluateAsync(app, """{"variables":{"weight":3}}""")!)!.GetValue<decimal>().ShouldBe(5m);
        (await EvaluateAsync(app, """{"variables":{"weight":10}}""")!)!.GetValue<decimal>().ShouldBe(20m);
    }

    [Fact]
    public async Task FR_DMN_003_redeploying_the_same_file_keeps_the_version_and_a_change_creates_a_new_one()
    {
        await using var app = await TestApp.StartAsync(postgres);
        await DeployAsync(app, Dmn);

        var same = JsonNode.Parse(await (await DeployAsync(app, Dmn)).Content.ReadAsStringAsync())!["decisions"]![0]!;
        var changed = JsonNode.Parse(await (await DeployAsync(app, Dmn.Replace("weight * 2", "weight * 3", StringComparison.Ordinal))).Content.ReadAsStringAsync())!["decisions"]![0]!;

        same["version"]!.GetValue<int>().ShouldBe(1);
        same["isNewVersion"]!.GetValue<bool>().ShouldBeFalse();
        changed["version"]!.GetValue<int>().ShouldBe(2);
        (await EvaluateAsync(app, """{"variables":{"weight":10}}""")!)!.GetValue<decimal>().ShouldBe(30m);
    }

    [Fact]
    public async Task FR_DMN_003_unknown_decisions_are_404_and_malformed_files_are_rejected()
    {
        await using var app = await TestApp.StartAsync(postgres);

        var missing = await app.Client.PostAsync(new Uri("/v1/decisions/nope/evaluate", UriKind.Relative), new StringContent("{}", Encoding.UTF8, "application/json"));
        var bad = await DeployAsync(app, "<definitions");

        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
