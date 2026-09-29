# Connectors

The `IDocumentSource` extension point and the filesystem reference connector.

`IDocumentSource` is the extension point. A connector enumerates documents out
of a system the customer already runs, converts them to Markdown so the
structural chunker can see headings, and supplies each document's access: from
the source system's own permissions where the connector can read them, and
otherwise as `DocumentAccess.NoOne`, which leaves the document indexed and
unreachable. The filesystem connector reads no permissions; its access comes
from the folder rules an administrator sets (see [Access](access.md)).

`FileSystemSource` ships as the reference implementation: every file in a
folder tree outside hidden files and folders, handed to the reader that
claims its extension (Markdown and plain text built in, PDF and Word through
first-party extensions), with access supplied per path. It takes no
credentials, which makes it the one connector that can be demonstrated on a
laptop.
