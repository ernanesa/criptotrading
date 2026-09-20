---
name: local-hybrid-graphrag
description: Build and use a local hybrid RAG with an incremental knowledge graph for project requirements, documentation, code, decisions, and ClickUp context while minimizing token usage.
metadata:
  short-description: Local hybrid RAG with project graph
---

# Local Hybrid Graph RAG

Use this skill when the project needs durable context across long Codex sessions or when requirements, code, documentation, decisions, and ClickUp items must stay connected.

## Architecture

- **Canonical sources**: repository files, dependency manifests, ADRs, OpenAPI, approved requirements, and explicitly selected ClickUp records.
- **Lexical index**: SQLite FTS5 for exact terms, identifiers, routes, package names, and error messages.
- **Vector index**: local embeddings for semantic retrieval, keyed by source hash and model name.
- **Metadata filters**: project, bounded context, feature, source type, version, status, authority, and freshness.
- **Knowledge graph**: entities and edges such as `epic CONTAINS story`, `story IMPLEMENTED_BY task`, `task AFFECTS file`, `endpoint DOCUMENTED_BY spec`, `decision CONSTRAINS story`, and `item SUPERSEDES item`.
- **Reranker/context builder**: combine lexical, semantic, graph-neighborhood, authority, and freshness scores, then produce a small cited context pack.

## Ingestion

Ingest incrementally. Hash every source and skip unchanged content. Chunk by semantic boundaries such as headings, ADR decisions, API operations, classes, components, and ClickUp items; never split a contract in the middle without preserving its parent metadata. Extract links, IDs, code symbols, routes, package names, and explicit supersession markers into graph edges.

## Retrieval

1. Classify the question and identify the feature/bounded context.
2. Apply metadata filters before search.
3. Run lexical and vector search in parallel.
4. Expand one or two graph hops only around high-confidence entities.
5. Rerank by authority, version compatibility, freshness, and direct relevance.
6. Return only the smallest sufficient context with source references and conflicts.

Do not use the graph as an authority by itself; it is a navigation and consistency layer over source documents.

## Consistency and supersession

When a new decision replaces an old requirement, create a `SUPERSEDES` edge and exclude the old item from active retrieval while retaining provenance. Invalidate affected summaries and embeddings only, not the entire index.

## Token policy

Use compact context packs, cached summaries, and diffs. Retrieve full source only when the model needs exact wording or implementation evidence. Luna receives bounded packs; Terra receives expanded graph neighborhoods only for ambiguity, architecture, or high-risk changes.

Read [references/storage-and-scoring.md](references/storage-and-scoring.md) for the initial local design.
