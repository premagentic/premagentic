#!/bin/bash
# Starts PremAgentic for a trial in one container and hands stdin and stdout
# to the stdio MCP bridge. See the Dockerfile at the repository root.
#
# 1. PostgreSQL, on its socket only (listen_addresses is empty).
# 2. A first run registers one person and one agent and issues the agent a
#    token, kept in a file only this account can read.
# 3. The API, on 127.0.0.1:18080 only (8080 is left free for a proxy in
#    front of the bridge), with no search role (a trial has one database
#    role; a deployment made by `prem setup` has its search role).
# 4. The bridge, given the token file, replaces this script.
#
# stdout is the MCP channel, so until the bridge starts everything printed
# here and by the programs goes to stderr.
set -eu

# The official postgres image starts this as postgres, with the PostgreSQL
# programs on PATH. A plain Debian image with Debian's postgresql package
# starts it as root and keeps them in /usr/lib/postgresql/<major>/bin. Both
# work: as root, the folders are given to postgres and this runs again as it.
if [ "$(id -u)" = 0 ]; then
    mkdir -p /var/lib/postgresql/data /var/run/postgresql
    chown postgres:postgres /var/lib/postgresql /var/lib/postgresql/data /var/run/postgresql
    exec runuser -u postgres -- "$0" "$@"
fi
for bin in /usr/lib/postgresql/*/bin; do
    if [ -d "$bin" ]; then PATH="$bin:$PATH"; fi
done
export PATH="/opt/premagentic/bin:$PATH"

exec 3>&1 1>&2

data=/var/lib/postgresql/data
state=/var/lib/postgresql/premagentic
mkdir -p "$state"
chmod 700 "$state"

if [ ! -s "$data/PG_VERSION" ]; then
    initdb --username=prem --auth=trust --pgdata="$data" >/dev/null
fi
pg_ctl --pgdata="$data" --wait --silent --options="-c listen_addresses=''" start
if ! psql --username=prem --dbname=postgres --tuples-only --no-align \
        --command="select 1 from pg_database where datname = 'premagentic'" | grep -q 1; then
    createdb --username=prem premagentic
fi

export PREM_CONNECTION_STRING="Host=/var/run/postgresql;Username=prem;Database=premagentic"
export PREM_ALLOW_NO_SEARCH_ROLE=1

token="$state/agent.token"
if [ ! -s "$token" ]; then
    prem users add owner --display "Owner"
    prem agents add assistant --owner owner --mode service --model local
    umask 077
    prem tokens issue assistant --days 365 | tail -n 1 > "$token"
fi

ASPNETCORE_URLS=http://127.0.0.1:18080 Premagentic.Api &

# The postgres image has no curl, so /health is asked over bash's own /dev/tcp.
healthy() {
    (exec 4<>/dev/tcp/127.0.0.1/18080 \
        && printf 'GET /health HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n' >&4 \
        && head -n 1 <&4 | grep -q ' 200 ') 2>/dev/null
}
tries=0
until healthy; do
    tries=$((tries + 1))
    if [ "$tries" -gt 120 ]; then
        echo "premagentic-try: the API did not answer /health within 60 seconds" >&2
        exit 1
    fi
    sleep 0.5
done

exec 1>&3 3>&-
PREM_API_URL=http://127.0.0.1:18080 PREM_AGENT_TOKEN_FILE="$token" exec Premagentic.McpServer
