# Initial local design

## Storage

Start with SQLite as the local source of truth:

- `documents`: source, path, version, hash, authority, updated_at.
- `chunks`: document_id, heading, text, token_estimate, embedding_model.
- `fts_chunks`: SQLite FTS5 index over chunk text and identifiers.
- `embeddings`: chunk_id, model, vector payload or a local vector extension table.
- `entities`: type, canonical_name, source_refs.
- `edges`: from_entity, relation, to_entity, source_ref, confidence.
- `summaries`: scope, source_hash, text, model, created_at.

Use a local embedding provider when available; otherwise keep lexical retrieval functional. Avoid introducing a hosted vector database until local scale or team sharing proves it necessary.

## Retrieval score

Use a weighted score that can be tuned empirically:

```text
score = 0.30 lexical
      + 0.30 semantic
      + 0.20 graph proximity
      + 0.10 authority
      + 0.10 freshness
```

Apply hard filters for project and version compatibility before scoring. Penalize superseded, conflicting, or stale sources. Keep the score explainable so a bad retrieval can be diagnosed.

## Safe first milestone

Implement ingestion, hashes, FTS5, metadata filters, source citations, and supersession edges first. Add local embeddings next. Add graph expansion and reranking after measuring retrieval failures. This preserves a useful system even when an embedding model or graph extraction is unavailable.
