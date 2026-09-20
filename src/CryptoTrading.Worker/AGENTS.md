# Worker Runtime Rules

- Preserve `NO_TRADE`, SafeMode, KillSwitch, durable checkpoint and reconciliation gates.
- Runtime transitions must fail closed; restart and reconnect behavior needs targeted tests.
- A worker change must not make authenticated registration or mutable exchange behavior the default.
