# Expressions (flowd-FEEL)

An expression starts with `=`; text without it is a plain string. Expressions are side-effect free and bounded by a step and time budget.

## Syntax
Literals (numbers, strings, `true`, `false`, `null`), lists `[1, 2]`, contexts `{a: 1}`, arithmetic `+ - * / **`, comparisons, `and` / `or`, `if ... then ... else ...`, `between ... and ...`, path `order.customer.name`, filters `items[price > 10]` and indexes `items[1]`, membership `x in [1, 2]` and intervals `x in [1..5]` / `(1..5)` / `[1..5)`, quantifiers `some x in xs satisfies x > 1`, `every x in xs satisfies x > 1`, and `for x in xs return x * 2`.

## Functions
- Strings: `contains`, `starts with`, `ends with`, `upper case`, `lower case`, `string length`, `substring`, `substring before`, `substring after`, `replace`, `matches`, `split`, `string join`, `string`, `number`.
- Lists: `count`, `sum`, `min`, `max`, `mean`, `median`, `product`, `append`, `concatenate`, `reverse`, `sort`, `index of`, `distinct values`, `flatten`, `list contains`, `remove`, `insert before`, `sublist`, `union`, `all`, `any`.
- Numbers: `abs`, `floor`, `ceiling`, `round`, `round up`, `round down`, `round half up`, `round half down`, `modulo`, `sqrt`, `exp`, `log`, `even`, `odd`.
- Dates: `now`, `today`, `date and time`, `duration`, `year`, `month`, `day`, `weekday`.
- Contexts: `get value`, `context put`, `context merge`.
- Other: `not`, `is defined`.

Regular expressions have a 100 ms match timeout; a pattern that runs longer fails the evaluation instead of stalling the engine.

## Not supported yet
User-defined functions, date/time arithmetic on `date`/`time` types, years-and-months durations, `instance of`, and the less common standard functions (`stddev`, `mode`, `list replace`, ...). Check `docs/camunda-parity.md`.
