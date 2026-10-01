# syntax=docker/dockerfile:1.7
#
# PremAgentic in one container, for trying the MCP server: the 0.1.0 release
# archive over a stock PostgreSQL in the same container. The stdio bridge
# (Premagentic.McpServer) is what the container runs on stdin and stdout; it
# passes the API's read-only tools through, so the API and its database run
# beside it. See docker/try-entrypoint.sh for what starts, and in what order.
#
# This is a trial and not a deployment. A deployment runs `prem setup` against
# the PostgreSQL the customer already runs, over HTTPS, with a search role and
# generated passwords: INSTALL.txt in the archive, and the manual's
# install page. Here the database listens on no network address, only on its
# socket inside the container, and its data is gone when the container is.
#
#   docker build -t premagentic-try .
#   docker run -i --rm premagentic-try
#
# The image comes from the release archive, checked against the SHA-256 the
# release published in SHA256SUMS, so it holds the same programs and model
# that release does.

FROM postgres:17.11@sha256:d74eeac9a635390a49bc21bd49fccd973de707e2a53a76ac49b552b8712ec46f

ARG PREM_VERSION=0.1.0
ADD --checksum=sha256:737a77acd64672665c654588c959a858dded00178c7b57436f1706617ba828db \
    https://github.com/premagentic/premagentic/releases/download/v${PREM_VERSION}/premagentic-${PREM_VERSION}-linux-x64.tar.gz \
    /tmp/premagentic.tar.gz

RUN mkdir -p /opt/premagentic \
 && tar -xzf /tmp/premagentic.tar.gz -C /opt/premagentic --strip-components=1 \
 && rm /tmp/premagentic.tar.gz

COPY --chmod=0755 docker/try-entrypoint.sh /usr/local/bin/premagentic-try

# The image's own postgres account owns the data folder and the socket folder,
# so everything here runs as it and nothing runs as root.
USER postgres
ENV PATH=/opt/premagentic/bin:$PATH

ENTRYPOINT ["/usr/local/bin/premagentic-try"]
