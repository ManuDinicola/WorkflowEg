# Inclusive join (FR-EXEC-005)

**Diverging.** Every outgoing flow whose condition is true gets a token. If none is true the default flow is used. If there is no default flow, an incident `CONDITION_ERROR` is raised on the gateway.

**Converging.** A token that arrives at an inclusive gateway with several incoming flows waits. After every command, once all queued steps have run, the engine checks each waiting gateway:

1. Take the live element instances in the same scope (the process, or one sub-process) that are *not* arrivals at this gateway.
2. For each of them, walk the static sequence flows forward from its element (breadth first, each element visited once).
3. If any of them can reach the gateway, someone may still arrive: keep waiting.
4. Otherwise the gateway fires: all waiting arrivals are completed, the outgoing conditions are evaluated once and the chosen flows get one token each.

**Consequences**
- A branch that was never taken (its condition was false) has no live element, so it never blocks the join (T-INCL-02).
- A branch that is still running (job, nested sub-process, other gateway) blocks the join exactly as long as it can reach it (T-INCL-01).
- A token that is itself downstream of the gateway only blocks it if a loop leads back to the gateway.
- Reachability ignores conditions, so a branch that *could* reach the gateway but never will (an exclusive gateway that goes elsewhere) keeps the join waiting until that token finishes or the instance is cancelled. This is the usual, conservative behavior.
- The check runs only when the queue is empty, so it never fires early in the middle of a command.
- If the gateway raises an incident it is not re-evaluated until the incident is resolved.
