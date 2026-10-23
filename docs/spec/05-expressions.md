## 8. Expression language

### 8.1 Choice
**FR-EXPR-001** Implement a **FEEL subset** ("flowd-FEEL") behind the interface `Evaluator.Eval(expr string, ctx Context) (Value, error)`. If a maintained FEEL library exists for the chosen language, wrap it; otherwise implement the subset below. Static expressions are plain literals; dynamic expressions start with `=` (e.g., `= order.total > 100`), as in Zeebe.

### 8.2 Supported subset (MUST)
- Literals: numbers (int/decimal), strings `"…"`, booleans, `null`, lists `[1,2]`, contexts `{a:1}`.
- Paths: `a.b.c`, `a[1]` (1-based), `a[item > 2]` (filter), `a.b[1].c`.
- Operators: `+ - * / ** `, `= != < <= > >=`, `and or not()`, `in`, `between … and …`, string `+`.
- `if … then … else …`.
- Built-ins (minimum): `count, sum, min, max, append, contains, starts with, ends with, upper case, lower case, substring, string length, number, string, is defined, date and time, now(), duration, abs, floor, ceiling, round`.
- ISO-8601 durations/dates in timers: `PT15M`, `P1D`, `R3/PT10M`, dates `2026-12-01T10:00:00Z`.

### 8.3 Variable scoping
- **FR-VAR-001** Lookup walks from the current scope up to the root. Write semantics: **set-in-defining-scope** (if variable exists in an ancestor scope, update there; else create in the scope where it's set).
- **FR-VAR-002** Variable values MUST be valid JSON. Max size per variable 1 MiB; max total per instance 16 MiB (configurable) → violation raises incident `VARIABLE_SIZE_EXCEEDED`.
- **FR-VAR-003** Reserved names: prefix `_flowd` (reserved).

### 8.4 Input/output mappings
- `<flowd:ioMapping><flowd:input source="= expr" target="name"/><flowd:output source="= expr" target="name"/></flowd:ioMapping>`.
- Inputs create **local** variables in the activity scope; outputs are evaluated against (job result variables ∪ local scope) and written to the **parent** scope.
- Without any output mapping, **all** result variables are propagated to the parent scope (Camunda 8 default; configurable via `flowd:propagateAll=false`).

### 8.5 Security
- **FR-EXPR-SEC-001** Evaluator MUST be side-effect free, non-Turing-complete (no unbounded loops/recursion), with an evaluation budget (max 100 000 steps, 100 ms wall time default).


