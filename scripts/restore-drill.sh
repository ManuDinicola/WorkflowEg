#!/usr/bin/env bash
# Backup and restore drill (FR-OPS-010): proves that a pg_dump of a running Flowd database restores into an empty database
# and that the restored copy is complete and usable. Used by CI; you can run it against a scratch PostgreSQL too.
#
#   PGHOST=localhost PGUSER=postgres PGPASSWORD=postgres FLOWD_DLL=src/Flowd.Host/bin/Release/net11.0/flowd.dll ./scripts/restore-drill.sh
#
# Needs: psql, pg_dump, pg_restore, curl, dotnet. Creates and drops the databases flowd_drill_src and flowd_drill_restored.
set -euo pipefail

: "${PGHOST:?set PGHOST}" "${PGUSER:?set PGUSER}" "${PGPASSWORD:?set PGPASSWORD}"
PGPORT="${PGPORT:-5432}"
FLOWD_DLL="${FLOWD_DLL:-src/Flowd.Host/bin/Release/net11.0/flowd.dll}"
WORK="$(mktemp -d)"
SERVER_PID=""
trap 'if [ -n "$SERVER_PID" ]; then kill "$SERVER_PID" 2>/dev/null || true; fi; rm -rf "$WORK"; psql -q -d postgres -c "DROP DATABASE IF EXISTS flowd_drill_src" -c "DROP DATABASE IF EXISTS flowd_drill_restored" >/dev/null 2>&1 || true' EXIT

conn() { echo "Host=$PGHOST;Port=$PGPORT;Database=$1;Username=$PGUSER;Password=$PGPASSWORD;Maximum Pool Size=10"; }

serve() { # serve <database> <port>
  Flowd__ConnectionString="$(conn "$1")" Flowd__Auth__Insecure=true ASPNETCORE_URLS="http://127.0.0.1:$2" \
    dotnet "$FLOWD_DLL" serve --auto-migrate >"$WORK/server-$1.log" 2>&1 &
  SERVER_PID=$!
  for _ in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:$2/readyz" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  echo "Flowd did not become ready on $1"; tail -30 "$WORK/server-$1.log"; exit 1
}

stop() { kill "$SERVER_PID"; wait "$SERVER_PID" 2>/dev/null || true; SERVER_PID=""; }

cat >"$WORK/drill.bpmn" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
  <bpmn:process id="drill" isExecutable="true">
    <bpmn:startEvent id="S"/>
    <bpmn:serviceTask id="T"><bpmn:extensionElements><flowd:taskDefinition type="drill-work"/></bpmn:extensionElements></bpmn:serviceTask>
    <bpmn:endEvent id="E"/>
    <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="T"/><bpmn:sequenceFlow id="f2" sourceRef="T" targetRef="E"/>
  </bpmn:process>
</bpmn:definitions>
EOF

psql -q -d postgres -c "DROP DATABASE IF EXISTS flowd_drill_src" -c "DROP DATABASE IF EXISTS flowd_drill_restored" -c "CREATE DATABASE flowd_drill_src" -c "CREATE DATABASE flowd_drill_restored"

echo "1. run Flowd on the source database and create some state"
serve flowd_drill_src 18181
curl -fsS -F "resources=@$WORK/drill.bpmn" http://127.0.0.1:18181/v1/deployments >/dev/null
KEY="$(curl -fsS -H 'Content-Type: application/json' -d '{"bpmnProcessId":"drill","variables":{"order":42}}' http://127.0.0.1:18181/v1/process-instances | sed -E 's/.*"processInstanceKey":"([0-9]+)".*/\1/')"
[ -n "$KEY" ] || { echo "no instance key"; exit 1; }
echo "   instance $KEY started"
stop

echo "2. back up and restore into an empty database"
pg_dump -Fc -d flowd_drill_src -f "$WORK/flowd.dump"
pg_restore --no-owner -d flowd_drill_restored "$WORK/flowd.dump"

echo "3. the restored database needs no migration and holds the instance"
APPLIED="$(Flowd__ConnectionString="$(conn flowd_drill_restored)" dotnet "$FLOWD_DLL" migrate)"
echo "   $APPLIED"
[ "$APPLIED" = "Applied 0 migration(s)." ] || { echo "the restored database was not current"; exit 1; }
serve flowd_drill_restored 18182
STATE="$(curl -fsS "http://127.0.0.1:18182/v1/process-instances/$KEY" | sed -E 's/.*"state":"([A-Z]+)".*/\1/')"
[ "$STATE" = "ACTIVE" ] || { echo "restored instance state is '$STATE', expected ACTIVE"; exit 1; }
VARS="$(curl -fsS "http://127.0.0.1:18182/v1/process-instances/$KEY/variables")"
echo "$VARS" | grep -q '"order":42' || { echo "restored variables are wrong: $VARS"; exit 1; }

echo "4. the restored copy can do new work: activate and complete the job of the restored instance"
JOB="$(curl -fsS -H 'Content-Type: application/json' -d '{"type":"drill-work","worker":"drill","timeoutMs":30000,"maxJobs":1,"requestTimeoutMs":1000}' http://127.0.0.1:18182/v1/jobs/activate | sed -E 's/.*"key":"([0-9]+)".*/\1/')"
curl -fsS -o /dev/null -H 'Content-Type: application/json' -d '{"variables":{"done":true},"worker":"drill"}' "http://127.0.0.1:18182/v1/jobs/$JOB/complete"
STATE="$(curl -fsS "http://127.0.0.1:18182/v1/process-instances/$KEY" | sed -E 's/.*"state":"([A-Z]+)".*/\1/')"
[ "$STATE" = "COMPLETED" ] || { echo "after the job the instance is '$STATE', expected COMPLETED"; exit 1; }
stop
echo "restore drill passed"
