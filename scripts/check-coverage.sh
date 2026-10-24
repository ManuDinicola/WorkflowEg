#!/bin/sh
# Fails if coverage is below NFR-006: engine core >= 90 %, overall >= 80 %.
# Usage: check-coverage.sh <Cobertura.xml>
set -eu

file="${1:?usage: check-coverage.sh <Cobertura.xml>}"
overall_min=80
engine_min=90

# Overall line rate is the first line-rate attribute on the root <coverage> element.
overall=$(grep -o -m1 '<coverage [^>]*line-rate="[0-9.]*"' "$file" | grep -o 'line-rate="[0-9.]*"' | grep -o '[0-9.]*' | head -n1)
# Engine core = the Flowd.Engine package element.
engine=$(grep -o '<package name="Flowd.Engine"[^>]*line-rate="[0-9.]*"' "$file" | grep -o 'line-rate="[0-9.]*"' | grep -o '[0-9.]*' | head -n1)
engine=${engine:-1}

pct() { awk -v r="$1" 'BEGIN { printf "%.1f", r * 100 }'; }
below() { awk -v v="$1" -v m="$2" 'BEGIN { exit !(v < m) }'; }

overall_pct=$(pct "$overall")
engine_pct=$(pct "$engine")
echo "coverage: overall=${overall_pct}% (min ${overall_min}), engine=${engine_pct}% (min ${engine_min})"

status=0
if below "$overall_pct" "$overall_min"; then echo "FAIL: overall coverage below ${overall_min}%"; status=1; fi
if below "$engine_pct" "$engine_min"; then echo "FAIL: engine core coverage below ${engine_min}%"; status=1; fi
exit $status
