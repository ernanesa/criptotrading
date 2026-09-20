---
name: versioned-docs-workflow
description: Use version-matched official documentation and Context7 when analyzing, specifying, implementing, or reviewing .NET, C#, React, TypeScript, and project dependencies.
metadata:
  short-description: Version-aware documentation workflow
---

# Versioned Documentation Workflow

Use this skill whenever a task depends on framework, language, runtime, package, API, or tool documentation.

## Workflow

1. Inspect the repository's dependency manifests and lockfiles before searching.
2. Record the exact runtime, framework, language, and package versions relevant to the task.
3. Prefer the official documentation for that version. Use Context7 only for targeted retrieval when it has the needed library/version context.
4. Retrieve the smallest useful set of passages and preserve source names, versions, and relevant API names.
5. Implement or write the specification using the project's existing patterns; do not invent a dependency or API shape from memory.
6. Run the project's build, tests, type checks, lint, and formatting commands that apply.
7. Report documentation sources, version assumptions, validations, conflicts, and remaining uncertainty.

## Token and cache policy

Cache compact summaries keyed by source URL, package name, and version. Invalidate when dependency manifests, lockfiles, framework versions, or source documents change. Do not cache volatile release status or ClickUp data without a freshness check. Prefer diffs and focused re-reviews over sending complete documentation repeatedly.

## Source policy

Official documentation is authoritative. Community examples are illustrative only and must not override the installed API contract. If the installed version and retrieved documentation disagree, stop and report the conflict before coding.

Read [references/checklist.md](references/checklist.md) for the compact review checklist.
