using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Flowd.Contracts.Execution;
using Flowd.Load;
using Flowd.Worker.Client;

// End-to-end load test: starts N instances of a one-step process and works the jobs, against one or several Flowd nodes that share a database.
//   dotnet run -c Release --project bench/Flowd.Load -- --urls http://127.0.0.1:5097,http://127.0.0.1:5098 --instances 2000 --starters 16 --workers 8
var options = Args.Parse(args);
var urls = options.Urls.Select(u => new Uri(u)).ToArray();
var clients = urls.Select(u => new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 512, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { BaseAddress = u, Timeout = TimeSpan.FromSeconds(60) }).ToArray();

const string Process = """
    <?xml version="1.0" encoding="UTF-8"?>
    <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
      <bpmn:process id="load" isExecutable="true">
        <bpmn:startEvent id="S"/>
        <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="load-work"/></bpmn:extensionElements></bpmn:serviceTask>
        <bpmn:endEvent id="E"/>
        <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
      </bpmn:process>
    </bpmn:definitions>
    """;

using (var form = new MultipartFormDataContent())
using (var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Process)))
{
    form.Add(file, "resources", "load.bpmn");
    (await clients[0].PostAsync(new Uri("/v1/deployments", UriKind.Relative), form)).EnsureSuccessStatusCode();
}

var total = Stopwatch.StartNew();
using var stop = new CancellationTokenSource();
var workers = new List<JobWorker>();
var running = new List<Task>();
for (var i = 0; i < options.Workers; i++)
{
    var client = new FlowdClient(clients[i % clients.Length]);
    var worker = client.CreateWorker("load-work", (_, _) => Task.FromResult(JobResults.Complete()), new JobWorkerOptions { WorkerName = $"load-{i}", MaxConcurrentJobs = 8, PollTimeout = TimeSpan.FromMilliseconds(300) });
    workers.Add(worker);
    running.Add(worker.RunAsync(stop.Token));
}

var startLatencies = new List<double>(options.Instances);
var next = 0;
var starters = Enumerable.Range(0, options.Starters).Select(s => Task.Run(async () =>
{
    var http = clients[s % clients.Length];
    while (Interlocked.Increment(ref next) <= options.Instances)
    {
        var timer = Stopwatch.StartNew();
        var response = await http.PostAsJsonAsync(new Uri("/v1/process-instances", UriKind.Relative), new StartInstanceRequestDto("load", null, null, null, null));
        response.EnsureSuccessStatusCode();
        lock (startLatencies)
        {
            startLatencies.Add(timer.Elapsed.TotalMilliseconds);
        }
    }
})).ToArray();
await Task.WhenAll(starters);
var startedAt = total.Elapsed;

var completed = 0;
while (completed < options.Instances && total.Elapsed < TimeSpan.FromMinutes(10))
{
    var summary = await clients[0].GetFromJsonAsync<JsonObject>(new Uri("/v1/analytics/summary", UriKind.Relative));
    completed = summary?["instancesByState"]?["COMPLETED"]?.GetValue<int>() ?? 0;
    await Task.Delay(200);
}

total.Stop();
await stop.CancelAsync();
await Task.WhenAll(running);
startLatencies.Sort();
double Percentile(double p) => startLatencies.Count == 0 ? 0 : startLatencies[Math.Min(startLatencies.Count - 1, (int)(startLatencies.Count * p))];
Console.WriteLine($"nodes={urls.Length} instances={options.Instances} starters={options.Starters} workers={options.Workers}");
Console.WriteLine($"completed={completed} all started after {startedAt.TotalSeconds:0.0}s, all completed after {total.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine($"throughput={completed / total.Elapsed.TotalSeconds:0.0} instances/s (each = 1 start + 1 job activate + 1 complete)");
Console.WriteLine($"start latency ms: p50={Percentile(0.5):0.0} p95={Percentile(0.95):0.0} p99={Percentile(0.99):0.0}");
Console.WriteLine($"jobs reported by workers={workers.Sum(w => w.Handled)}");
return completed == options.Instances ? 0 : 1;

