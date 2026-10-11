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

done_item_count=0
while IFS=$'\t' read -r done_id field value; do
    if [[ "$field" == 'code' ]]; then
        ((done_item_count += 1))
    fi

    case "$field" in
        code|tests|review|evidence|residual_risk|rollback)
            if [[ -z "${value//[[:space:]]/}" || "$value" == '[]' ]]; then
                errors+=("Done item $done_id is missing required link: $field")
                continue
            fi

            link_values="${value//[\[\],]/ }"
            read -r -a links <<<"$link_values"
            if (( ${#links[@]} == 0 )); then
                errors+=("Done item $done_id is missing required link: $field")
                continue
            fi

            for link in "${links[@]}"; do
                link="${link#\"}"
                link="${link%\"}"
                link="${link#\'}"
                link="${link%\'}"
                case "$link" in
                    ''|/*|*://*|../*|*/../*|..)
                        errors+=("Done item $done_id has an invalid $field link")
                        continue
                        ;;
                esac

                resolved_link="$(realpath -e -- "$repo_root/$link" 2>/dev/null || true)"
                if [[ "$resolved_link" != "$repo_root/"* ]]; then
                    errors+=("Done item $done_id has a non-existent $field link")
                fi
            done
            ;;
        command|observed_result)
            if [[ -z "${value//[[:space:]]/}" || "$value" == '[]' ]]; then
                errors+=("Done item $done_id is missing required field: $field")
            fi
            ;;
        *)
            errors+=("Done item $done_id is missing required field: $field")
            ;;
    esac
done < <(
    awk '
        function trim(value) {
            sub(/^[[:space:]]+/, "", value)
            sub(/[[:space:]]+$/, "", value)
            return value
        }
        function flush() {
            if (current_id != "" && tolower(status) == "done") {
                for (field_index = 1; field_index <= required_count; field_index++) {
                    field = required_fields[field_index]
                    printf "%s\t%s\t%s\n", current_id, field, fields[field]
                }
            }
            delete fields
            current_id = ""
            status = ""
        }
        BEGIN {
            required_count = split("code tests command observed_result review evidence residual_risk rollback", required_fields, " ")
        }
        /^[[:space:]]*-[[:space:]]+id:[[:space:]]*/ {
            flush()
            current_indent = match($0, /^[[:space:]]*/)
            current_indent = RLENGTH
            current_id = $0
            sub(/^[[:space:]]*-[[:space:]]+id:[[:space:]]*/, "", current_id)
            current_id = trim(current_id)
            next
        }
        current_id != "" {
            field_indent = match($0, /^[[:space:]]*/)
            field_indent = RLENGTH
            if (field_indent != current_indent + 2) {
                next
            }

            field = $0
            sub(/^[[:space:]]*/, "", field)
            if (field !~ /^(status|code|tests|command|observed_result|review|evidence|residual_risk|rollback):/) {
                next
            }

            value = field
            sub(/^[^:]*:[[:space:]]*/, "", value)
            sub(/:.*/, "", field)
            fields[field] = trim(value)
            if (field == "status") {
                status = fields[field]
            }
        }
        END {
            flush()
        }
    ' "$manifest"
)

if (( ${#errors[@]} > 0 )); then
    echo "Traceability validation failed:" >&2
    printf ' - %s\n' "${errors[@]}" >&2
    exit 1
fi

printf 'Traceability validation passed: %d explicit item IDs, schema v2, no duplicate/orphan references, %d Done items with complete local evidence links.\n' "${#item_ids[@]}" "$done_item_count"
