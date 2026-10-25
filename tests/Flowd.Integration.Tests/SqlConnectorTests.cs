using System.Text.Json.Nodes;

namespace Flowd.Integration.Tests;

/// <summary>FR-CON-017: the SQL connector queries and changes a PostgreSQL database through bound parameters.</summary>
[Trait("Category", "Integration")]
public sealed class SqlConnectorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static string Process(string id, string settings) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="{id}" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="Q"><bpmn:extensionElements><flowd:connector type="sql" connection="main" {settings}/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="Q"/><bpmn:sequenceFlow id="f2" sourceRef="Q" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task<JsonNode?> WaitAsync(TestApp app, string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var state = (await app.GetInstanceAsync(key)).State;
            if (state == "COMPLETED")
            {
                return await app.GetVariablesAsync(key);
            }

            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task<string> FirstIncidentAsync(TestApp app, string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var incidents = JsonNode.Parse(await app.Client.GetStringAsync(new Uri($"/v1/incidents?processInstanceKey={key}", UriKind.Relative), timeout.Token))!["items"]!.AsArray();
            if (incidents.Count > 0)
            {
                return incidents[0]!["errorMessage"]!.GetValue<string>();
            }

            await Task.Delay(200, timeout.Token);
        }
    }

    [Fact]
    public async Task FR_CON_017_a_query_returns_rows_and_parameters_are_bound_not_pasted_into_the_statement()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false);
        await using var configured = await TestApp.StartAsync(postgres, secure: false, $"--Flowd:Connectors:Sql:Connections:main:ConnectionString={app.ConnectionString}");
        await configured.DeployAsync(Process("q", "query=\"select @n::int * 2 as doubled, @name::text as name\" parameters='{&quot;n&quot;:{{n}},&quot;name&quot;:&quot;{{name}}&quot;}' resultVariable=\"r\""));

        var key = await configured.StartInstanceAsync("q", """{"n":21,"name":"x'; drop table process_instance; --"}""");
        var variables = await WaitAsync(configured, key);

        var row = variables!["r"]!["rows"]![0]!;
        row["doubled"]!.GetValue<long>().ShouldBe(42);
        row["name"]!.GetValue<string>().ShouldBe("x'; drop table process_instance; --"); // the text arrived as a value; nothing was dropped
        variables["r"]!["rowCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task FR_CON_017_a_read_only_connection_refuses_statements_that_change_data_and_a_writable_one_runs_them()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false);
        await using var readOnly = await TestApp.StartAsync(postgres, secure: false, $"--Flowd:Connectors:Sql:Connections:main:ConnectionString={app.ConnectionString}");
        await readOnly.DeployAsync(Process("w", "mode=\"execute\" query=\"create table drill_t (id int)\""));
        var refused = await readOnly.StartInstanceAsync("w");

        (await FirstIncidentAsync(readOnly, refused)).ShouldContain("read-only");

        await using var writable = await TestApp.StartAsync(postgres, secure: false, $"--Flowd:Connectors:Sql:Connections:main:ConnectionString={app.ConnectionString}", "--Flowd:Connectors:Sql:Connections:main:ReadOnly=false");
        await writable.DeployAsync(Process("w2", "mode=\"execute\" query=\"create table drill_t (id int)\" resultVariable=\"a\""));
        var created = await WaitAsync(writable, await writable.StartInstanceAsync("w2"));
        await writable.DeployAsync(Process("w3", "mode=\"execute\" query=\"insert into drill_t (id) values (@id), (@id + 1)\" parameters='{&quot;id&quot;:{{id}}}' resultVariable=\"b\""));
        var inserted = await WaitAsync(writable, await writable.StartInstanceAsync("w3", """{"id":5}"""));

        created!["a"]!["affectedRows"]!.GetValue<int>().ShouldBe(-1);
        inserted!["b"]!["affectedRows"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task FR_CON_017_an_unknown_connection_name_ends_in_an_incident()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false);
        await app.DeployAsync(Process("u", "query=\"select 1\""));

        var key = await app.StartInstanceAsync("u");

        (await FirstIncidentAsync(app, key)).ShouldContain("not configured");
    }
}
