using Flowd.Api.Grpc;
using Grpc.Net.Client;

namespace Flowd.Integration.Tests;

/// <summary>FR-API-020: the gRPC gateway starts instances and runs jobs.</summary>
[Trait("Category", "Integration")]
public sealed class GrpcApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Process = """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
          <bpmn:process id="g" isExecutable="true">
            <bpmn:startEvent id="S"/>
            <bpmn:serviceTask id="T"><bpmn:extensionElements><flowd:taskDefinition type="grpc-work"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="E"/>
            <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="T"/><bpmn:sequenceFlow id="f2" sourceRef="T" targetRef="E"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static readonly string[] Endpoints =
    [
        "--Kestrel:Endpoints:Rest:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:Rest:Protocols=Http1",
        "--Kestrel:Endpoints:Grpc:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:Grpc:Protocols=Http2",
    ];

    [Fact]
    public async Task FR_API_020_a_client_starts_an_instance_and_a_worker_completes_its_job_over_grpc()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, Endpoints);
        await app.DeployAsync(Process);
        using var channel = GrpcChannel.ForAddress(app.Urls.Last());
        var gateway = new Gateway.GatewayClient(channel);

        var started = await gateway.StartInstanceAsync(new StartInstanceRequest { BpmnProcessId = "g", VariablesJson = """{"n":1}""" });
        var jobs = await gateway.ActivateJobsAsync(new ActivateJobsRequest { Type = "grpc-work", Worker = "w", MaxJobs = 5, TimeoutMs = 30000 });
        await gateway.CompleteJobAsync(new CompleteJobRequest { JobKey = jobs.Jobs.Single().Key, VariablesJson = """{"done":true}""", Worker = "w" });

        jobs.Jobs.Single().ProcessInstanceKey.ShouldBe(started.ProcessInstanceKey);
        (await app.GetInstanceAsync(started.ProcessInstanceKey)).State.ShouldBe("COMPLETED");
    }

    [Fact]
    public async Task FR_API_020_failures_become_grpc_status_codes()
    {
        await using var app = await TestApp.StartAsync(postgres, secure: false, Endpoints);
        using var channel = GrpcChannel.ForAddress(app.Urls.Last());
        var gateway = new Gateway.GatewayClient(channel);

        var unknown = await Should.ThrowAsync<Grpc.Core.RpcException>(() => gateway.StartInstanceAsync(new StartInstanceRequest { BpmnProcessId = "nope" }).ResponseAsync);
        var bad = await Should.ThrowAsync<Grpc.Core.RpcException>(() => gateway.ActivateJobsAsync(new ActivateJobsRequest()).ResponseAsync);

        unknown.StatusCode.ShouldBe(Grpc.Core.StatusCode.NotFound);
        bad.StatusCode.ShouldBe(Grpc.Core.StatusCode.InvalidArgument);
    }
}
