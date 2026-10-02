# syntax=docker/dockerfile:1.7
#
# The stdio MCP bridge alone, for the MCP Registry: an assistant's client runs
# this image and talks MCP over its stdin and stdout, and the bridge passes the
# read-only tools of a PremAgentic API that already runs, the customer's own,
# through to it. It holds no index and no database credentials; see
# src/Premagentic.McpServer/ApiBridge.cs and docs/connect-an-assistant.md.
#
#   docker run -i --rm \
#     -e PREM_API_URL=https://prem.example.org:8443 \
#     -e PREM_AGENT_TOKEN=prem_agt_... \
#     ghcr.io/premagentic/premagentic-mcp:0.1.0
#
# An API whose certificate `prem setup` signed itself is trusted by mounting
# that certificate and naming it, as OpenSSL reads it:
#
#     -v /etc/premagentic/https.crt:/certs/prem.crt:ro -e SSL_CERT_FILE=/certs/prem.crt
#
# The programs come from the release archive, checked against the SHA-256 the
# release published in SHA256SUMS. .github/workflows/publish-mcp.yml passes
# both for each release; the defaults are 0.1.0's.

FROM debian:trixie-slim@sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a

ARG PREM_VERSION=0.1.0
ARG PREM_ARCHIVE_SHA256=737a77acd64672665c654588c959a858dded00178c7b57436f1706617ba828db

# Only bin/ and the license texts are kept: the bridge reads no model and loads
# no extension.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && curl -fsSL -o /tmp/premagentic.tar.gz \
      "https://github.com/premagentic/premagentic/releases/download/v${PREM_VERSION}/premagentic-${PREM_VERSION}-linux-x64.tar.gz" \
 && echo "${PREM_ARCHIVE_SHA256}  /tmp/premagentic.tar.gz" | sha256sum -c - \
 && mkdir -p /opt/premagentic \
 && tar -xzf /tmp/premagentic.tar.gz -C /opt/premagentic --strip-components=1 \
 && rm -rf /tmp/premagentic.tar.gz /opt/premagentic/models /opt/premagentic/extensions \
      /opt/premagentic/samples /opt/premagentic/sample-docs /opt/premagentic/deploy \
 && apt-get purge -y curl \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

# The MCP Registry checks that the image names the server it is listed as.
LABEL io.modelcontextprotocol.server.name="io.github.premagentic/premagentic"
LABEL org.opencontainers.image.source="https://github.com/premagentic/premagentic"
LABEL org.opencontainers.image.licenses="AGPL-3.0-only"

USER 65532:65532
ENTRYPOINT ["/opt/premagentic/bin/Premagentic.McpServer"]
