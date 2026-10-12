# PremAgentic MCP bridge

The stdio MCP bridge to a PremAgentic API. An assistant whose client starts a
local program runs this; the bridge presents one registered agent's token to the
API's `/mcp` and passes the API's read-only tools, `search_knowledge` and
`get_document_section`, through to it. It holds no index and no database
credentials.

It needs a PremAgentic API that already runs, and the token of an agent
registered there (`prem tokens issue <agent>`). With the .NET 10 SDK:

```
dnx Premagentic.McpServer
```

with these set in the client's configuration for the server:

- `PREM_API_URL`: the API's address, such as `https://prem.example.org:8443`. Plain `http://` only for localhost.
- `PREM_AGENT_TOKEN_FILE`: a file holding the agent's token, or `PREM_AGENT_TOKEN` holding it directly.

PremAgentic is on-premises knowledge retrieval over the Markdown, text, PDF,
Word and Excel files an organization keeps, with the rules you set enforced
before anything is retrieved. Source, manual and releases:
https://github.com/premagentic/premagentic and https://premagentic.com.
Open source under the GNU Affero General Public License 3.0.

<!-- mcp-name: io.github.premagentic/premagentic -->
