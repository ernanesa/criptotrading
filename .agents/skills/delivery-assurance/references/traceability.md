# Traceability evidence model

Each delivery item should be identifiable by stable keys:

```text
EPIC-001
  STORY-001
    AC-001
      TASK-001
        CODE-<file-or-boundary>
        TEST-<test-name>
```

Minimum evidence for completion:

- acceptance criterion and expected behavior;
- implementation boundary or changed file;
- automated test or explicit reason a test is not applicable;
- validation command and result;
- code review status;
- documentation and migration impact;
- known residual risk and rollback/mitigation.

Use `supersedes` links when requirements change. Keep old evidence for audit but exclude superseded requirements from active generation and validation.
