using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dapper;
using Flowd.Worker.Client;
using Npgsql;

namespace Flowd.Integration.Tests;

/// <summary>T-CHAOS-01 (scaled down): the host is killed several times while workers run; nothing may be lost or duplicated.</summary>
[Trait("Category", "Integration")]
public sealed class ChaosTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const int Instances = 30;

    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="chaos" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="chaos-a"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:serviceTask id="B"><bpmn:extensionElements><flowd:taskDefinition type="chaos-b"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="B"/><bpmn:sequenceFlow id="f3" sourceRef="B" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public async Task T_CHAOS_01_killing_the_host_repeatedly_loses_and_duplicates_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var port = FreePort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var host = new HostProcess(connectionString, port);
        host.Start();
        var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        await RetryAsync(() => http.PostAsync(new Uri("/v1/deployments", UriKind.Relative), Multipart(Process)));

        using var stop = new CancellationTokenSource();
        var options = new JobWorkerOptions { WorkerName = "chaos", PollTimeout = TimeSpan.FromMilliseconds(300), JobTimeout = TimeSpan.FromSeconds(3), MaxConcurrentJobs = 4, MinBackoff = TimeSpan.FromMilliseconds(50), MaxBackoff = TimeSpan.FromMilliseconds(500) };
        var client = new FlowdClient(http);
        var workers = new[] { "chaos-a", "chaos-b" }.Select(type => client.CreateWorker(type, (_, _) => Task.FromResult(JobResults.Complete()), options)).ToArray();
        var runs = workers.Select(w => w.RunAsync(stop.Token)).ToArray();

        var starting = Enumerable.Range(0, Instances).Select(_ => RetryAsync(() => http.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new Flowd.Contracts.Execution.StartInstanceRequestDto("chaos", null, null, null, null)))).ToArray();
        for (var kill = 0; kill < 3; kill++)
        {
            await Task.Delay(Random.Shared.Next(400, 1200));
            host.Kill();
            await Task.Delay(300);
            host.Start();
        }

        await Task.WhenAll(starting);
        await WaitForAsync(connectionString, "SELECT count(*) FROM process_instance WHERE state <> 'COMPLETED'", 0, TimeSpan.FromSeconds(90));
        await stop.CancelAsync();
        await Task.WhenAll(runs);
        host.Kill();
        http.Dispose();

        await using var connection = new NpgsqlConnection(connectionString);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM process_instance")).ShouldBe(Instances);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM element_instance WHERE state NOT IN ('COMPLETED', 'TERMINATED')")).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM (SELECT element_instance_key FROM history_event WHERE event_type = 'JOB_COMPLETED' GROUP BY element_instance_key HAVING count(*) > 1) d")).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM job")).ShouldBe(Instances * 2);
    }

    private static async Task WaitForAsync(string connectionString, string sql, long expected, TimeSpan timeout)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        using var cts = new CancellationTokenSource(timeout);
        while (await connection.ExecuteScalarAsync<long>(sql) != expected)
        {
            await Task.Delay(200, cts.Token);
        }
    }

    private static async Task<HttpResponseMessage> RetryAsync(Func<Task<HttpResponseMessage>> send)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                var response = await send();
                if (response.IsSuccessStatusCode || ++attempt >= 60 || (int)response.StatusCode is < 500 and not 409)
                {
                    return response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && ++attempt < 60)
            {
                // host is down; try again
            }

            await Task.Delay(250);
        }
    }

    private static MultipartFormDataContent Multipart(string xml)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(xml), "resources", "chaos.bpmn");
        return content;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class HostProcess(string connectionString, int port)
    {
        private Process? _process;

        public void Start()
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "flowd.dll");
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.Environment["Flowd__ConnectionString"] = connectionString;
            info.Environment["Flowd__Auth__Insecure"] = "true";
            info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            foreach (var arg in new[] { dll, "serve", "--auto-migrate" })
            {
                info.ArgumentList.Add(arg);
            }

            _process = System.Diagnostics.Process.Start(info)!;
            _process.OutputDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public void Kill()
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
        }
    }
}
