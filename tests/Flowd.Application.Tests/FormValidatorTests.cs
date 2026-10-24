using System.Text.Json.Nodes;
using Flowd.Application.Forms;

namespace Flowd.Application.Tests;

/// <summary>Form validation of submitted user task variables (FR-UT-005).</summary>
public sealed class FormValidatorTests
{
    private static readonly JsonObject Form = JsonNode.Parse("""
        {
          "id": "order",
          "components": [
            { "type": "number", "key": "amount", "validate": { "required": true, "min": 1, "max": 1000 } },
            { "type": "textfield", "key": "name", "validate": { "minLength": 2, "maxLength": 5, "pattern": "^[A-Za-z]+$" } },
            { "type": "checkbox", "key": "express" },
            { "type": "checklist", "key": "extras" },
            { "type": "group", "components": [ { "type": "textfield", "key": "note", "validate": { "required": true } } ] },
            { "type": "text", "text": "Static text without a key" },
            { "type": "somethingnew", "key": "other" }
          ]
        }
        """)!.AsObject();

    private static string[] Codes(string variables) =>
        [.. FormValidator.Validate(Form, JsonNode.Parse(variables)!.AsObject()).Select(p => $"{p.Target}:{p.Code}")];

    [Fact]
    public void FR_UT_005_a_valid_submission_has_no_problems() =>
        Codes("""{"amount":10,"name":"Bob","express":true,"extras":["a"],"note":"x","other":123}""").ShouldBeEmpty();

    [Fact]
    public void FR_UT_005_required_fields_are_reported_including_those_in_groups_and_empty_strings() =>
        Codes("""{"note":""}""").ShouldBe(["amount:FORM_REQUIRED", "note:FORM_REQUIRED"], ignoreOrder: true);

    [Theory]
    [InlineData("""{"amount":0,"note":"x"}""", "amount:FORM_MIN")]
    [InlineData("""{"amount":1001,"note":"x"}""", "amount:FORM_MAX")]
    [InlineData("""{"amount":"ten","note":"x"}""", "amount:FORM_TYPE")]
    [InlineData("""{"amount":5,"note":"x","name":"B"}""", "name:FORM_MIN_LENGTH")]
    [InlineData("""{"amount":5,"note":"x","name":"Bobbie"}""", "name:FORM_MAX_LENGTH")]
    [InlineData("""{"amount":5,"note":"x","name":"B0b"}""", "name:FORM_PATTERN")]
    [InlineData("""{"amount":5,"note":"x","express":"yes"}""", "express:FORM_TYPE")]
    [InlineData("""{"amount":5,"note":"x","extras":"a"}""", "extras:FORM_TYPE")]
    public void FR_UT_005_each_rule_reports_its_own_code(string variables, string expected) =>
        Codes(variables).ShouldBe([expected]);

    [Fact]
    public void FR_UT_005_no_variables_at_all_still_reports_the_required_fields() =>
        FormValidator.Validate(Form, null).Count.ShouldBe(2);

    [Fact]
    public void FR_UT_005_a_pattern_that_cannot_run_never_accepts_the_value()
    {
        var form = JsonNode.Parse("""{"components":[{"type":"textfield","key":"x","validate":{"pattern":"("}}]}""")!.AsObject();

        FormValidator.Validate(form, JsonNode.Parse("""{"x":"a"}""")!.AsObject()).Single().Code.ShouldBe("FORM_PATTERN");
    }
}
