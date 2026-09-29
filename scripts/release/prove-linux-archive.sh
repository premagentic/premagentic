#!/usr/bin/env bash
# Proves that the Linux release archive installs and works on a machine that has
# neither .NET nor a network route out, by following its INSTALL.txt:
#
#   scripts/release/prove-linux-archive.sh <premagentic-X.Y.Z-linux-x64.tar.gz>
#
# On this machine's Docker it makes a network created with --internal (no route
# out of the host), a stock postgres:17.11 on it, and a bare ubuntu:24.04 beside it
# that is given nothing but the archive. Inside the Ubuntu container it shows that
# there is no dotnet, no system ICU and no route out, then unpacks the archive into
# /opt/premagentic as INSTALL.txt says, and runs, in INSTALL.txt's words: prem
# --version, prem setup with the first administrator, the starter profile and the
# ingest of its two sources, then the PDF, Word and Excel readers the archive ships,
# allowed by hash with prem extensions allow, reading an invented PDF and Word file
# and the sample equipment register workbook, and the API. An agent then searches
# over MCP with its token from a client container, finds the handbook, the salary
# bands, the PDF's second page, the Word file and the workbook's Keys sheet, and
# the gate holds both ways; and the stdio MCP bridge, which shares
# bin/ and its one runtime with the other two programs, is started from the archive
# and finds the PDF through the API. No step sets PREM_ONNX_MODEL_DIR: the
# programs must find the model inside the archive, and with no route out nothing
# could have been downloaded.
#
# Two controls. Before the readers are allowed, the same ingest of the invented
# files counts every one as skipped, so the readers are what read them. At the
# end, the model folder is moved away and the same search is run again; it must
# fail at once with the one line that names where the model was looked for, so the
# proof above could have failed.
#
# It prints PASS or FAIL for each check and exits non-zero on the first failure.
# Everything it creates is removed on exit. No password or token is printed or put
# on a command line.
#
# Needs: Docker, bash (Linux, macOS, or Git Bash on Windows) and python3 for the
# invented files (scripts/clean-install/make-reader-samples.py). Not the .NET SDK.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
archive=${1:?usage: prove-linux-archive.sh <premagentic-X.Y.Z-linux-x64.tar.gz>}
[ -f "$archive" ] || { printf 'no such file: %s\n' "$archive" >&2; exit 2; }
name=$(basename "$archive")
[[ $name =~ ^premagentic-(.+)-linux-x64\.tar\.gz$ ]] || { printf '%s is not a linux-x64 release archive\n' "$name" >&2; exit 2; }
version=${BASH_REMATCH[1]}

run_id="prem-archive-$(date +%s)-$RANDOM"
network="$run_id-net"
db="$run_id-db"
app="$run_id-app"
curl_image=curlimages/curl:8.22.0@sha256:58adaa4e8dca9c988bae2aba4ab3434a0bb2da16bbe3f92dec39ec7785166777
# The proof's images, pinned by digest (docker buildx imagetools inspect).
pg_image=postgres:17.11@sha256:d74eeac9a635390a49bc21bd49fccd973de707e2a53a76ac49b552b8712ec46f
ubuntu_image=ubuntu:24.04@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3
work=$(mktemp -d)

# Git Bash rewrites arguments that look like POSIX paths; the container-side paths
# must reach docker unchanged.
export MSYS_NO_PATHCONV=1
host_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

cleanup() {
  docker rm -f "$app" "$db" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

step() { printf '\n== %s\n' "$*"; }
pass() { printf 'PASS  %s\n' "$*"; }
fail() { printf 'FAIL  %s\n' "$*" >&2; exit 1; }
# Runs a command in the application container with nothing set but what the
# command itself sets.
in_app() { docker exec "$app" bash -c "$1"; }
as_app='export PREM_CREDENTIALS_FILE=/etc/premagentic/app.credentials;'

docker pull -q "$curl_image" >/dev/null
docker pull -q "$ubuntu_image" >/dev/null
docker pull -q "$pg_image" >/dev/null

step "A network with no route out, a stock PostgreSQL on it"
admin_password=$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 32)
docker network create --internal "$network" >/dev/null
docker run -d --name "$db" --network "$network" -e POSTGRES_PASSWORD="$admin_password" "$pg_image" >/dev/null
for _ in $(seq 1 60); do docker exec "$db" pg_isready -U postgres -h localhost >/dev/null 2>&1 && break; sleep 1; done
docker exec "$db" pg_isready -U postgres -h localhost >/dev/null 2>&1 || fail "postgres:17.11 did not become ready"
pass "postgres:17.11 is up on an --internal network"

# INSTALL.txt step 2: a file whose one line names a role that can create roles and
# databases, readable by its owner only.
( umask 077
  printf 'connection=Host=%s;Username=postgres;Password=%s;Database=postgres\n' "$db" "$admin_password" > "$work/admin.credentials"
  head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24 > "$work/first-admin.password"; printf '\n' >> "$work/first-admin.password" )
unset admin_password

step "A bare ubuntu:24.04 given nothing but the archive"
docker create --name "$app" --network "$network" "$ubuntu_image" sleep infinity >/dev/null
docker cp "$(host_path "$archive")" "$app:/root/$name"
docker cp "$(host_path "$work/admin.credentials")" "$app:/root/admin.credentials"
docker cp "$(host_path "$work/first-admin.password")" "$app:/root/first-admin.password"
docker start "$app" >/dev/null
in_app 'chmod 600 /root/admin.credentials /root/first-admin.password'
if in_app 'command -v dotnet || ls -d /usr/share/dotnet /usr/lib/dotnet /opt/dotnet 2>/dev/null' | grep -q .; then
  fail "the container has a dotnet; the proof would not show the archive needs none"
fi
if in_app 'ls /usr/lib/x86_64-linux-gnu/libicu* /lib/x86_64-linux-gnu/libicu* 2>/dev/null' | grep -q .; then
  fail "the container has a system libicu; the proof would not show the archive carries its own"
fi
if in_app 'timeout 5 bash -c "exec 3<>/dev/tcp/1.1.1.1/443"' >/dev/null 2>&1; then fail "the container reached 1.1.1.1:443; the network has a route out"; fi
if in_app 'timeout 5 getent hosts example.com' >/dev/null 2>&1; then fail "the container resolved example.com"; fi
pass "no dotnet, no system libicu, no route to 1.1.1.1:443 and no outside name resolution"

step "INSTALL.txt step 1: unpack into /opt/premagentic"
in_app "mkdir -p /opt/premagentic && tar -xzf /root/$name -C /opt/premagentic --strip-components=1"
in_app 'test -x /opt/premagentic/bin/prem && test -x /opt/premagentic/bin/Premagentic.Api && test -x /opt/premagentic/bin/Premagentic.McpServer \
  && test -f /opt/premagentic/models/minilm/model.onnx' || fail "the unpacked archive lacks the programs or the model"
[ "$(in_app 'find /opt/premagentic -name libcoreclr.so | wc -l')" = 1 ] || fail "the unpacked archive does not carry exactly one runtime"
pass "unpacked; the three programs in bin/ are executable over one runtime, and the model is in /opt/premagentic/models/minilm"

in_app '/opt/premagentic/bin/prem --version' > "$work/version.out" 2>&1 || { cat "$work/version.out"; fail "prem --version failed"; }
grep -qF -- "$version" "$work/version.out" || { cat "$work/version.out"; fail "prem --version does not print $version"; }
pass "prem --version prints $(head -n 1 "$work/version.out" | tr -d '\r')"

step "INSTALL.txt step 3: prem setup"
in_app "cd /root && /opt/premagentic/bin/prem setup --admin-connection-file /root/admin.credentials \
  --credentials-dir /etc/premagentic --admin-user first-admin --admin-password-file /root/first-admin.password \
  --host-name $app" > "$work/setup.out" 2>&1 || { cat "$work/setup.out"; fail "prem setup failed"; }
grep -q "Setup complete" "$work/setup.out" || { cat "$work/setup.out"; fail "prem setup did not report completion"; }
grep -q "\[applied\] administrator .*made 'first-admin' the first administrator" "$work/setup.out" || { cat "$work/setup.out"; fail "the first administrator was not made"; }
grep -q "model files present in /opt/premagentic/models/minilm" "$work/setup.out" || { cat "$work/setup.out"; fail "setup did not find the model inside the archive"; }
if grep -qF -e "$(head -n 1 "$work/first-admin.password")" "$work/setup.out"; then fail "the administrator's password appears in setup's output"; fi
pass "setup completed from a working folder outside the archive, using the model in /opt/premagentic/models/minilm"

step "INSTALL.txt step 5: the starter profile and its two sources"
in_app "$as_app cd /root && /opt/premagentic/bin/prem profile apply /opt/premagentic/samples/profiles/starter \
  && /opt/premagentic/bin/prem ingest --source open && /opt/premagentic/bin/prem ingest --source hr" > "$work/ingest.out" 2>&1 \
  || { cat "$work/ingest.out"; fail "the profile or the ingest failed"; }
grep -q "open/handbook.md" "$work/ingest.out" && grep -q "hr/salary-bands.md" "$work/ingest.out" \
  || { cat "$work/ingest.out"; fail "the ingest did not index the sample documents"; }
pass "profile applied; open/handbook.md and hr/salary-bands.md indexed"

step "The PDF, Word and Excel readers shipped in the archive"
# An invented PDF, Word file and macro-enabled name, generated here each run and
# never committed, and the repository's invented equipment register workbook,
# given to the container outside the archive.
python3 "$(host_path "$repo/scripts/clean-install/make-reader-samples.py")" "$(host_path "$work/formats")" >/dev/null \
  || fail "the invented reader samples could not be written"
cp "$repo/sample-docs/spreadsheets/equipment-register.xlsx" "$work/formats/"
docker cp "$(host_path "$work/formats")" "$app:/root/formats"
# INSTALL.txt step 6: the extensions folder is a setting, which the API reads too.
in_app "$as_app cd /root && /opt/premagentic/bin/prem settings set extensions.folder /opt/premagentic/extensions" > "$work/folder.out" 2>&1 \
  || { cat "$work/folder.out"; fail "setting extensions.folder failed"; }
as_readers="$as_app cd /root &&"
# The control: before the readers are allowed, the same ingest reads none of the
# four and counts each as skipped, so the readers are what read them below.
in_app "$as_readers /opt/premagentic/bin/prem ingest /root/formats --public --prefix formats" > "$work/formats-before.out" 2>&1 \
  || { cat "$work/formats-before.out"; fail "the ingest of the invented files before the readers were allowed failed"; }
grep -q "Skipped 4 file(s)" "$work/formats-before.out" \
  || { cat "$work/formats-before.out"; fail "before the readers were allowed, the four files were not all counted as skipped"; }
if grep -q -e "formats/loading-dock.pdf" -e "formats/visitor-policy.docx" -e "formats/equipment-register.xlsx" "$work/formats-before.out"; then
  cat "$work/formats-before.out"; fail "a file was indexed before any reader was allowed"
fi
pass "before the readers are allowed, the PDF, the Word file, the workbook and the macro-enabled name are all skipped"
in_app "$as_readers /opt/premagentic/bin/prem extensions allow /opt/premagentic/extensions/pdf-reader \
  && /opt/premagentic/bin/prem extensions allow /opt/premagentic/extensions/docx-reader \
  && /opt/premagentic/bin/prem extensions allow /opt/premagentic/extensions/xlsx-reader && /opt/premagentic/bin/prem extensions list" \
  > "$work/extensions.out" 2>&1 || { cat "$work/extensions.out"; fail "allowing the readers from the archive failed"; }
grep -qE "^  pdf-reader  [0-9a-fA-F]{64}  \(loaded\)" "$work/extensions.out" \
  && grep -qE "^  docx-reader  [0-9a-fA-F]{64}  \(loaded\)" "$work/extensions.out" \
  && grep -qE "^  xlsx-reader  [0-9a-fA-F]{64}  \(loaded\)" "$work/extensions.out" \
  && grep -q "^Refused: none\." "$work/extensions.out" \
  || { cat "$work/extensions.out"; fail "the readers are not all three allowed and loaded from the archive, or an extension was refused"; }
in_app "$as_readers /opt/premagentic/bin/prem ingest /root/formats --public --prefix formats" > "$work/formats.out" 2>&1 \
  || { cat "$work/formats.out"; fail "the ingest of the invented files through the readers failed"; }
grep -q "formats/loading-dock.pdf" "$work/formats.out" && grep -q "formats/visitor-policy.docx" "$work/formats.out" \
  && grep -q "formats/equipment-register.xlsx" "$work/formats.out" \
  || { cat "$work/formats.out"; fail "the PDF, the Word file and the workbook were not all indexed"; }
grep -qF ".docm (macro-enabled) 1" "$work/formats.out" \
  || { cat "$work/formats.out"; fail "the macro-enabled file was not counted as skipped for that reason"; }
pass "allowed by hash from inside the archive, the readers indexed the PDF, the Word file and the workbook and skipped the macro-enabled one"

step "INSTALL.txt step 4: the API"
docker exec -d "$app" bash -c "cd /root && exec env PREM_CREDENTIALS_FILE=/etc/premagentic/app.credentials /opt/premagentic/bin/Premagentic.Api > /root/api.log 2>&1"
for _ in $(seq 1 90); do in_app 'exec 3<>/dev/tcp/127.0.0.1/8443' >/dev/null 2>&1 && break; sleep 1; done
in_app 'cat /root/api.log' > "$work/api.log"
in_app 'exec 3<>/dev/tcp/127.0.0.1/8443' >/dev/null 2>&1 || { cat "$work/api.log"; fail "the API is not listening on 8443"; }
pass "the API started from the archive and listens on 8443"

docker cp "$app:/etc/premagentic/https.crt" "$(host_path "$work/https.crt")"
curl_to() {
  docker run -i --rm --network "$network" -v "$(host_path "$work/https.crt"):/ca.crt:ro" "$curl_image" \
    --fail --silent --show-error --max-time 30 --cacert /ca.crt "$@"
}
curl_to "https://$app:8443/health" < /dev/null > "$work/health.out" || fail "/health over HTTPS failed"
grep -q '"status":"ok"' "$work/health.out" || { cat "$work/health.out"; fail "/health did not answer ok"; }
pass "/health answers ok over HTTPS, the certificate and host name verified"

step "An agent searches over MCP with its token"
# The token goes to a private file in the container and from there to curl on its
# standard input; it never reaches this host's disk or a command line.
in_app "$as_app cd /root && /opt/premagentic/bin/prem agents add reader --owner first-admin --mode service --model local \
  && /opt/premagentic/bin/prem agents grant reader hr \
  && (umask 077; /opt/premagentic/bin/prem tokens issue reader --days 1 > token.out && tail -n 1 token.out > agent.token \
      && printf 'Authorization: Bearer %s\n' \"\$(cat agent.token)\" > agent.header; rm -f token.out)" \
  > "$work/agent.out" 2>&1 || { cat "$work/agent.out"; fail "making the agent and its token failed"; }
mcp_headers=(-H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream'
  -H 'MCP-Protocol-Version: 2026-07-28' -H 'Mcp-Method: tools/call' -H 'Mcp-Name: search_knowledge')
mcp_body() {
  printf '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_knowledge","arguments":{"query":"%s","topK":5},%s}}' \
    "$1" '"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}'
}
mcp_call() {
  docker exec "$app" cat /root/agent.header | curl_to -H @- "${mcp_headers[@]}" \
    --data-binary "$(mcp_body "$1")" "https://$app:8443/mcp" > "$work/mcp.out" \
    || { cat "$work/mcp.out"; fail "the MCP call for '$1' failed"; }
}
mcp_call "band four compensation review"
grep -q "hr/salary-bands.md" "$work/mcp.out" || { cat "$work/mcp.out"; fail "the agent granted hr did not get hr/salary-bands.md"; }
mcp_call "how long do I have to file an expense claim"
grep -q "open/handbook.md" "$work/mcp.out" || { cat "$work/mcp.out"; fail "the agent did not get open/handbook.md"; }
mcp_call "where do returns go"
grep -q "formats/loading-dock.pdf" "$work/mcp.out" && grep -q "Page 2" "$work/mcp.out" \
  || { cat "$work/mcp.out"; fail "over MCP, the returns question did not find the PDF's second page"; }
mcp_call "what badge do visitors wear"
grep -q "formats/visitor-policy.docx" "$work/mcp.out" && grep -q "Visitors" "$work/mcp.out" \
  || { cat "$work/mcp.out"; fail "over MCP, the badge question did not find the Word file under its heading"; }
mcp_call "where is the spare key to the cage kept"
# The answer is JSON on the wire, where ">" arrives escaped as >.
grep -q "formats/equipment-register.xlsx" "$work/mcp.out" && grep -qE 'equipment-register\.xlsx (>|\\u003E) Keys' "$work/mcp.out" \
  || { cat "$work/mcp.out"; fail "over MCP, the key question did not find the workbook under its Keys sheet"; }
in_app "$as_app cd /root && /opt/premagentic/bin/prem search 'band four compensation review' --as group:engineering" > "$work/gate.out" 2>&1 \
  || { cat "$work/gate.out"; fail "the search as engineering failed"; }
if grep -q "hr/salary-bands.md" "$work/gate.out"; then cat "$work/gate.out"; fail "engineering reached hr/salary-bands.md"; fi
grep -q "hits in" "$work/gate.out" || { cat "$work/gate.out"; fail "the search as engineering returned no result line"; }
pass "over MCP the agent granted hr finds the salary bands, the handbook, the PDF by its second page, the Word file and the workbook's Keys sheet; engineering does not reach the salary bands"

step "The MCP bridge from the archive, started as an assistant starts a local program"
# The bridge shares bin/ and its runtime with the other two programs, so it is run
# here, not assumed. It reads the agent's token from a file and presents it to the
# API, and trusts setup's certificate through SSL_CERT_FILE, as a client machine
# given https.crt would. A bash coprocess speaks MCP to it over its standard input
# and output: initialize, then a search through the API.
cat > "$work/bridge.sh" <<'EOF'
set -u
export PREM_API_URL="https://$1:8443" PREM_AGENT_TOKEN_FILE=/root/agent.token SSL_CERT_FILE=/etc/premagentic/https.crt
coproc BRIDGE { exec /opt/premagentic/bin/Premagentic.McpServer 2> /root/bridge.err; }
send() { printf '%s\n' "$1" >&"${BRIDGE[1]}"; }
answer() {
  local line
  while IFS= read -r -t 90 line <&"${BRIDGE[0]}"; do
    case $line in *"\"id\":$1,"* | *"\"id\":$1}"*) printf '%s\n' "$line"; return 0 ;; esac
  done
  printf 'no answer to request %s\n' "$1"
  return 1
}
send '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"archive-proof","version":"0"}}}'
answer 1 || exit 1
send '{"jsonrpc":"2.0","method":"notifications/initialized"}'
send '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_knowledge","arguments":{"query":"where do returns go","topK":5}}}'
answer 2 || exit 1
pid=${BRIDGE_PID:-}
eval "exec ${BRIDGE[1]}>&-"
[ -z "$pid" ] || wait "$pid" || true
EOF
docker cp "$(host_path "$work/bridge.sh")" "$app:/root/bridge.sh"
in_app "cd /root && timeout 180 bash /root/bridge.sh $app" > "$work/bridge.out" 2>&1 || { cat "$work/bridge.out"; fail "the bridge did not answer"; }
in_app 'cat /root/bridge.err' > "$work/bridge.err" 2>&1 || true
if grep -q 'prem_agt_' "$work/bridge.out" "$work/bridge.err"; then fail "the agent's token appears in the bridge's output"; fi
sed -n 1p "$work/bridge.out" | grep -q '"name":"premagentic"' && sed -n 1p "$work/bridge.out" | grep -qF -- "$version" \
  || { cat "$work/bridge.out" "$work/bridge.err"; fail "the bridge's initialize did not name premagentic and $version"; }
sed -n 2p "$work/bridge.out" | grep -q "formats/loading-dock.pdf" && ! sed -n 2p "$work/bridge.out" | grep -q '"isError":true' \
  || { cat "$work/bridge.out" "$work/bridge.err"; fail "through the bridge, the returns question did not find the PDF"; }
in_app 'rm -f /root/agent.header /root/agent.token'
pass "the bridge started from bin/, answered initialize as premagentic $version, and through the API found the PDF with the agent's token"

step "The control: the same search without the model folder"
in_app 'mv /opt/premagentic/models/minilm /opt/premagentic/models/minilm.away'
set +e
in_app "$as_app cd /root && timeout 60 /opt/premagentic/bin/prem search 'band four compensation review' --as group:hr" > "$work/control.out" 2>&1
control_exit=$?
set -e
in_app 'mv /opt/premagentic/models/minilm.away /opt/premagentic/models/minilm'
[ "$control_exit" != 0 ] || { cat "$work/control.out"; fail "the search succeeded without the model folder; the proof above could not have failed"; }
[ "$control_exit" != 124 ] || { cat "$work/control.out"; fail "the search without the model hung instead of failing"; }
grep -q "needs model.onnx and vocab.txt in a models/minilm folder, and none was found. Looked in: .*/opt/premagentic/models/minilm" "$work/control.out" \
  || { cat "$work/control.out"; fail "the search without the model failed, but not with the line naming where it looked"; }
if grep -qE 'Unhandled exception|^   at ' "$work/control.out"; then cat "$work/control.out"; fail "the refusal printed a stack trace"; fi
pass "without the model folder the search stops, exit $control_exit, with: $(grep -m 1 -o 'The local embedding provider needs.*none was found' "$work/control.out")"

printf '\nPASS  %s installs and works with no .NET and no network\n' "$name"
