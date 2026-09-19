#!/usr/bin/env bash
set -Eeuo pipefail

# Dependency-light validator for promotion artifacts. It never grants approval;
# it only proves that a human-approved artifact is structurally complete.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prm="${1:-$repo_root/docs/readiness/PRM-20260919-foundation.md}"
canary="${2:-$repo_root/docs/readiness/CANARY-20260919-draft.md}"
risk_policy="${3:-}"
errors=()
readiness_as_of_utc="${READINESS_AS_OF_UTC:-$(date -u +%F)}"

require_file() { [[ -f "$1" ]] || errors+=("required file not found: $1"); }
require_text() {
    local file="$1" pattern="$2" message="$3"
    grep -Eiq -- "$pattern" "$file" || errors+=("$file: $message")
}
reject_placeholders() {
    local file="$1"
    grep -Eiq -- 'pendente|a definir|não definidos|não aprovad|TBD|XXXX|placeholder' "$file" && errors+=("$file: unresolved placeholder or pending approval marker") || true
    grep -Eq -- '^[[:space:]]*-[[:space:]]*\[[[:space:]]\]' "$file" && errors+=("$file: unchecked mandatory item") || true
}

validate_valid_until() {
    local file="$1"
    local field="$2"
    local value

    value="$(grep -Eio -- "${field}:[[:space:]]*[0-9]{4}-[0-9]{2}-[0-9]{2}" "$file" | head -n 1 | sed -E 's/.*:[[:space:]]*//')" || true
    if [[ ! "$value" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]]; then
        errors+=("$file: $field must be an ISO-8601 UTC date")
        return
    fi

    if ! date -u -d "$value" +%F >/dev/null 2>&1; then
        errors+=("$file: $field is not a calendar date")
        return
    fi

    if [[ "$value" < "$readiness_as_of_utc" ]]; then
        errors+=("$file: $field is expired as of $readiness_as_of_utc")
    fi
}

if [[ ! "$readiness_as_of_utc" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || ! date -u -d "$readiness_as_of_utc" +%F >/dev/null 2>&1; then
    errors+=("READINESS_AS_OF_UTC must be a valid ISO-8601 UTC date")
fi

require_file "$prm"
require_file "$canary"
[[ -n "$risk_policy" ]] || errors+=("risk policy path is required as the third argument")
[[ -z "$risk_policy" ]] || require_file "$risk_policy"

if (( ${#errors[@]} == 0 )); then
    require_text "$prm" '(^|[^[:alpha:]])(Decisão|Decision):[[:space:]]*(Go|Conditional)([^[:alpha:]]|$)' 'decision must be Go or Conditional'
    require_text "$prm" 'Owner([^:]*):[[:space:]]*[^[:space:]]' 'owner/reviewer/approver must be populated'
    require_text "$prm" '(Válida até|Valid until):[[:space:]]*[0-9]{4}-[0-9]{2}-[0-9]{2}' 'valid-until date is required'
    require_text "$prm" '(threshold|limite)' 'threshold definitions are required'
    require_text "$prm" '(unit|unidade)' 'threshold units are required'
    require_text "$prm" '(window|janela)' 'threshold windows are required'
    require_text "$canary" '(Status|status):[[:space:]]*(Approved|Go|Conditional)' 'canary status must be approved'
    require_text "$canary" 'Owner([^:]*):[[:space:]]*[^[:space:]]' 'canary owner/approver must be populated'
    require_text "$canary" '(capital|notional).*[0-9]' 'capital/notional envelope must be numeric'
    require_text "$canary" '(Válida até|Valid until):[[:space:]]*[0-9]{4}-[0-9]{2}-[0-9]{2}' 'canary validity is required'
    require_text "$risk_policy" '(Version|Versão):[[:space:]]*[^[:space:]]+' 'RiskPolicy version is required'
    require_text "$risk_policy" '(Hash|SHA-256):[[:space:]]*[0-9a-f]{64}' 'RiskPolicy SHA-256 hash is required'
    require_text "$risk_policy" '(Approved by|Aprovador):[[:space:]]*[^[:space:]]+' 'RiskPolicy approver is required'
    require_text "$risk_policy" '(Valid until|Válida até):[[:space:]]*[0-9]{4}-[0-9]{2}-[0-9]{2}' 'RiskPolicy validity is required'
    require_text "$risk_policy" '(threshold|limite).*[0-9]' 'RiskPolicy numeric thresholds are required'
    validate_valid_until "$prm" '(Válida até|Valid until)'
    validate_valid_until "$canary" '(Válida até|Valid until)'
    validate_valid_until "$risk_policy" '(Valid until|Válida até)'
    for file in "$prm" "$canary" "$risk_policy"; do reject_placeholders "$file"; done
fi

if (( ${#errors[@]} > 0 )); then
    echo "Readiness validation failed:" >&2
    printf ' - %s\n' "${errors[@]}" >&2
    exit 1
fi
printf 'Readiness validation passed: PRM, CanaryPlan and RiskPolicy are structurally complete; human approval remains authoritative.\n'
