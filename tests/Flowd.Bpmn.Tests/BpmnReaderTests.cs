using System.Text;
using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;

namespace Flowd.Bpmn.Tests;

/// <summary>FR-DEP-004 (validation), FR-SEC-006 (XXE) and the parser of section 6.1.</summary>
public sealed class BpmnReaderTests
{
    private static BpmnReadResult ReadFixture(string name)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "testdata", name));
        return BpmnReader.Read(stream);
    }

    private static BpmnReadResult ReadText(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return BpmnReader.Read(stream);
    }

    private static IReadOnlyList<BpmnIssue> IssuesOf(BpmnReadResult result) => result switch
    {
        BpmnDefinitions => [],
        BpmnInvalid invalid => invalid.Issues,
    };

    [Fact]
    public void FR_DEP_004_valid_order_process_parses_into_the_model()
    {
        var result = ReadFixture("order-simple.bpmn");

        var definitions = result switch
        {
            BpmnDefinitions d => d,
            BpmnInvalid i => throw new InvalidOperationException(string.Join("; ", i.Issues.Select(x => x.Message))),
        };
        var process = definitions.Processes.ShouldHaveSingleItem();
        process.Id.ShouldBe("order-process");
        process.Nodes.Count.ShouldBe(5);
        process.Flows.Count.ShouldBe(5);
        process.Nodes.OfType<ServiceTask>().Select(t => t.TaskType).ShouldBe(["charge-card", "review-order"]);
        process.Nodes.OfType<ExclusiveGateway>().Single().DefaultFlowId.ShouldBe("F4");
        process.Flows.Single(f => f.Id == "F3").Condition.ShouldBe("= order.total > 1000");
    }

    [Fact]
    public void T_DEP_04_unsupported_element_is_rejected_with_element_id_and_reason()
    {
        var issues = IssuesOf(ReadFixture("complex-gateway.bpmn"));

        var issue = issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(BpmnIssueCodes.UnsupportedElement);
        issue.ElementId.ShouldBe("Complex");
        issue.Message.ShouldContain("complexGateway");
    }

    [Fact]
    public void T_DEP_05_dangling_references_are_all_reported_in_one_result()
    {
        var issues = IssuesOf(ReadFixture("dangling-and-more.bpmn"));

        issues.Select(i => i.Code).ShouldContain(BpmnIssueCodes.DanglingReference);
        issues.Where(i => i.Code == BpmnIssueCodes.DanglingReference).Select(i => i.ElementId).ShouldBe(["F2", "F3"]);
        issues.ShouldContain(i => i.Code == BpmnIssueCodes.MissingTaskType && i.ElementId == "Task");
    }

    [Fact]
    public void T_DEP_06_external_entities_are_rejected()
    {
        var issue = IssuesOf(ReadFixture("xxe.bpmn")).ShouldHaveSingleItem();

        issue.Code.ShouldBe(BpmnIssueCodes.DtdNotAllowed);
    }

    [Fact]
    public void FR_DEP_004_malformed_xml_is_reported_not_thrown()
    {
        var issue = IssuesOf(ReadText("<bpmn:definitions")).ShouldHaveSingleItem();

        issue.Code.ShouldBe(BpmnIssueCodes.MalformedXml);
    }

    [Fact]
    public void FR_DEP_004_file_without_process_is_rejected()
    {
        var xml = "<definitions xmlns=\"http://www.omg.org/spec/BPMN/20100524/MODEL\"/>";

        IssuesOf(ReadText(xml)).ShouldContain(i => i.Code == BpmnIssueCodes.NoProcess);
    }

    [Fact]
    public void FR_DEP_004_duplicate_ids_missing_start_and_non_executable_are_reported()
    {
        var xml = """
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <process id="p" isExecutable="false">
                <manualTask id="A"/><manualTask id="A"/>
              </process>
              <process id="p" isExecutable="true"><startEvent id="S"/></process>
              <process isExecutable="true"><startEvent id="S2"/></process>
            </definitions>
            """;

        var codes = IssuesOf(ReadText(xml)).Select(i => i.Code).ToList();

        codes.ShouldContain(BpmnIssueCodes.DuplicateId);
        codes.ShouldContain(BpmnIssueCodes.NoStartEvent);
        codes.ShouldContain(BpmnIssueCodes.NotExecutable);
        codes.ShouldContain(BpmnIssueCodes.DuplicateProcessId);
        codes.ShouldContain(BpmnIssueCodes.MissingProcessId);
    }

    [Fact]
    public void FR_DEP_004_gateway_flows_need_conditions_and_valid_default()
    {
        var xml = """
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <process id="p" isExecutable="true">
                <startEvent id="S"/>
                <exclusiveGateway id="G" default="Nope"/>
                <endEvent id="E1"/><endEvent id="E2"/>
                <sequenceFlow id="F0" sourceRef="S" targetRef="G"/>
                <sequenceFlow id="F1" sourceRef="G" targetRef="E1"/>
                <sequenceFlow id="F2" sourceRef="G" targetRef="E2"/>
                <sequenceFlow id="F3" sourceRef="S" targetRef="E1"><conditionExpression>= true</conditionExpression></sequenceFlow>
              </process>
            </definitions>
            """;

        var issues = IssuesOf(ReadText(xml));

        issues.ShouldContain(i => i.Code == BpmnIssueCodes.InvalidDefaultFlow && i.ElementId == "G");
        issues.Where(i => i.Code == BpmnIssueCodes.MissingCondition).Select(i => i.ElementId).ShouldBe(["F1", "F2"]);
        issues.ShouldContain(i => i.Code == BpmnIssueCodes.ConditionNotAllowed && i.ElementId == "F3");
    }

    [Fact]
    public void FR_DEP_004_terminate_end_event_and_non_none_start_events_are_distinguished()
    {
        var xml = """
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <process id="p" isExecutable="true">
                <startEvent id="S"/>
                <startEvent id="T"><timerEventDefinition/></startEvent>
                <endEvent id="E"><terminateEventDefinition/></endEvent>
              </process>
            </definitions>
            """;

        var issue = IssuesOf(ReadText(xml)).ShouldHaveSingleItem();

        issue.Code.ShouldBe(BpmnIssueCodes.UnsupportedElement);
        issue.ElementId.ShouldBe("T");
        issue.Message.ShouldContain("timerEventDefinition");
    }
}
