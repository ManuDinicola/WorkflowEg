# Forms

A user task can point to a form with `<flowd:formDefinition formKey="approval"/>`. Forms are JSON files in the Camunda form schema shape:
```
{ "id": "approval", "components": [
  { "type": "number",    "key": "amount", "label": "Amount", "validate": { "required": true, "min": 1, "max": 5000 } },
  { "type": "textfield", "key": "reason", "validate": { "required": true, "minLength": 3, "pattern": "^[A-Za-z ]+$" } },
  { "type": "checkbox",  "key": "urgent" } ] }
```
- Deploy: `curl -F resources=@approval.form http://localhost:8080/v1/forms` (same file again keeps the version, a change creates the next one). `GET /v1/forms` lists, `GET /v1/forms/{formId}` returns the latest schema.
- Render: `GET /v1/user-tasks/{key}/form` returns the schema of the task's form, so a UI can draw it.
- Submit: `POST /v1/user-tasks/{key}/complete` with the field values as `variables`. When the task has a deployed form, the values are checked first; problems answer 400 with one entry per field (`FORM_REQUIRED`, `FORM_TYPE`, `FORM_MIN`, `FORM_MAX`, `FORM_MIN_LENGTH`, `FORM_MAX_LENGTH`, `FORM_PATTERN`) and the task stays open. A task whose form is not deployed completes without checks.

Validated component types: `number`, `textfield`, `textarea`, `checkbox`, `checklist`, `taglist`, `select`, `radio`; groups are searched recursively; other components are ignored. Flowd stores and validates forms; it does not render them (there is no UI yet).
