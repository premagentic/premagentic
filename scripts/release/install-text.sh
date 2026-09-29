#!/usr/bin/env bash
# Prints an archive's INSTALL.txt from its template: the version put in, and the
# lines between @EXTENSIONS@ and @END-EXTENSIONS@ (what extensions/ is, and the
# step that allows its readers) kept only when the archive carries extensions.
#
#   scripts/release/install-text.sh <install-linux.txt|install-windows.txt> <version> <with-extensions|no-extensions>
#
# build.sh runs it for each archive; test.sh checks both ways.
set -euo pipefail

template=${1:?usage: install-text.sh <template> <version> <with-extensions|no-extensions>}
version=${2:?usage: install-text.sh <template> <version> <with-extensions|no-extensions>}
case ${3:?usage: install-text.sh <template> <version> <with-extensions|no-extensions>} in
  with-extensions) keep='/^@\(END-\)\{0,1\}EXTENSIONS@$/d' ;;
  no-extensions) keep='/^@EXTENSIONS@$/,/^@END-EXTENSIONS@$/d' ;;
  *) printf 'the third argument is with-extensions or no-extensions, not %s\n' "$3" >&2; exit 2 ;;
esac
[ -f "$template" ] || { printf 'no such template: %s\n' "$template" >&2; exit 2; }
sed -e "$keep" -e "s/@VERSION@/$version/g" "$template"
