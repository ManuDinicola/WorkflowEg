using Flowd.Bpmn.Model;

namespace Flowd.Bpmn.Reading;

/// <summary>Entry point: parse and validate BPMN XML in one call.</summary>
public static class BpmnReader
{
    public static BpmnReadResult Read(Stream xml)
    {
        var definitions = BpmnParser.TryParse(xml, out var issue);
        if (definitions is null)
        {
            return new BpmnInvalid([issue!]); // TryParse always sets issue when it returns null
        }

        var issues = BpmnValidator.Validate(definitions);
        return issues.Count == 0 ? definitions : new BpmnInvalid(issues);
    }
}
