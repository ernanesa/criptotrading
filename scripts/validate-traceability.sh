#!/usr/bin/env bash
set -Eeuo pipefail

# Lightweight, dependency-free validator for the canonical traceability
# manifest. It validates the repository's explicit YAML shape without trying
# to become a permissive YAML parser.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="${1:-$repo_root/plan/traceability.yaml}"
backlog="$repo_root/plan/17-backlog.md"
acceptance="$repo_root/plan/18-acceptance-criteria.md"
functional_requirements="$repo_root/plan/02-functional-requirements.md"
nonfunctional_requirements="$repo_root/plan/03-non-functional-requirements.md"

if [[ ! -f "$manifest" ]]; then
    echo "Traceability validation failed: manifest not found: $manifest" >&2
    exit 2
fi
for required_file in "$backlog" "$acceptance" "$functional_requirements" "$nonfunctional_requirements"; do
    if [[ ! -f "$required_file" ]]; then
        echo "Traceability validation failed: required source not found: $required_file" >&2
        exit 2
    fi
done

errors=()
if ! grep -Eq '^schema_version:[[:space:]]*2[[:space:]]*$' "$manifest"; then
    errors+=("schema_version must be 2")
fi
if grep -nEq '[A-Z]+-[0-9]+\.\.' "$manifest"; then
    errors+=("implicit numeric ranges are forbidden")
fi

mapfile -t item_ids < <(awk '/^[[:space:]]*-[[:space:]]+id:[[:space:]]*/ { print $NF }' "$manifest")
if (( ${#item_ids[@]} == 0 )); then
    errors+=("manifest contains no list item IDs")
fi

duplicate_ids="$(printf '%s\n' "${item_ids[@]}" | sort | uniq -d || true)"
if [[ -n "$duplicate_ids" ]]; then
    errors+=("duplicate IDs: $(tr '\n' ',' <<<"$duplicate_ids" | sed 's/,$//')")
fi

for id in "${item_ids[@]}"; do
    case "$id" in
        E-[0-9][0-9]|US-[0-9][0-9][0-9]|TASK-US-[0-9][0-9][0-9]-[0-9][0-9]) ;;
        *) errors+=("invalid item ID convention: $id") ;;
    esac
done

while IFS= read -r task_id; do
    [[ -z "$task_id" ]] && continue
    if ! grep -Eq "(^|[^A-Za-z0-9_-])${task_id}([^A-Za-z0-9_-]|$)" "$backlog"; then
        errors+=("task reference is absent from backlog: $task_id")
    fi
done < <(grep -E '^\s*task:[[:space:]]*TASK-US-' "$manifest" | sed -E 's/.*task:[[:space:]]*//')

for reference_prefix in AC RF RNF; do
    while IFS= read -r reference; do
        [[ -z "$reference" ]] && continue
        source_file="$acceptance"
        [[ "$reference_prefix" == RF ]] && source_file="$functional_requirements"
        [[ "$reference_prefix" == RNF ]] && source_file="$nonfunctional_requirements"
        if ! grep -Eq "(^|[^A-Za-z0-9_-])${reference}([^A-Za-z0-9_-]|$)" "$source_file"; then
            errors+=("$reference reference is absent from $source_file")
        fi
    done < <(grep -E '^[[:space:]]*(requirements|acceptance):' "$manifest" | grep -oE "${reference_prefix}-[A-Z0-9-]+" | sort -u)
done

if (( ${#errors[@]} > 0 )); then
    echo "Traceability validation failed:" >&2
    printf ' - %s\n' "${errors[@]}" >&2
    exit 1
fi

printf 'Traceability validation passed: %d explicit item IDs, schema v2, no duplicate/orphan references.\n' "${#item_ids[@]}"
