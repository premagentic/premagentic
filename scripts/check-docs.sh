#!/usr/bin/env bash
# Checks the manual's links and its index.
#
#   scripts/check-docs.sh [--allow-missing slug,slug]
#
# Over README.md and docs/*.md: every relative link and image resolves to a
# file, and every #anchor to a heading in the file it names. docs/toc.json and
# docs/README.md list the same slugs in the same order, every slug has a file,
# and each page's first heading is the title toc.json gives it. Fenced code is
# not scanned. Each failure is printed with its file and line, and the exit
# code is 1 when there was any and 0 when the manual is clean.
#
# --allow-missing names pages that are not written yet: their files may be
# absent and links to them are not followed. scripts/check-docs.ps1 does the
# same on PowerShell.
set -u
cd "$(dirname "$0")/.." || exit 2

allow=""
while [ $# -gt 0 ]; do
  case "$1" in
    --allow-missing) allow="${2:-}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
allowed() { case ",$allow," in *",$1,"*) return 0 ;; esac; return 1; }

failures=0
links=0
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

# The lines outside fenced code, each as "line<TAB>text".
unfenced() {
  awk 'BEGIN { f = 0 } /^[[:space:]]*```/ { f = !f; next } !f { printf "%d\t%s\n", NR, $0 }' "$1"
}

# The anchors GitHub gives a file's headings: lowercase, punctuation dropped,
# spaces to hyphens, and a repeated heading numbered -1, -2 and so on.
anchors() {
  unfenced "$1" | sed -n 's/^[0-9]*\t#\{1,6\} \(.*\)$/\1/p' \
    | tr 'A-Z' 'a-z' | sed 's/[^a-z0-9 _-]//g; s/ /-/g' \
    | awk '{ n = seen[$0]++; print (n ? $0 "-" n : $0) }'
}

# Every link and image target outside fenced code, as "line<TAB>target".
targets() {
  unfenced "$1" | awk -F '\t' '{
    line = $1; s = $2
    while (match(s, /\]\([^)]*\)/)) {
      print line "\t" substr(s, RSTART + 2, RLENGTH - 3)
      s = substr(s, RSTART + RLENGTH)
    }
  }'
}

# The first heading of a file, without its marks.
first_heading() {
  unfenced "$1" | sed -n 's/^[0-9]*\t# \(.*\)$/\1/p' | head -n 1
}

check_file() {
  local file="$1" dir line target path anchor resolved slug
  case "$file" in docs/*) dir="docs" ;; *) dir="." ;; esac
  while IFS=$'\t' read -r line target; do
    links=$((links + 1))
    case "$target" in http://*|https://*|mailto:*) continue ;; esac
    path="${target%%#*}"
    anchor=""
    case "$target" in *'#'*) anchor="${target#*#}" ;; esac
    if [ -n "$path" ]; then
      if [ "$dir" = "." ]; then resolved="$path"; else resolved="docs/$path"; fi
      resolved="${resolved#./}"
      if [ ! -f "$resolved" ]; then
        case "$resolved" in docs/*.md)
          slug="${resolved#docs/}"; slug="${slug%.md}"
          allowed "$slug" && continue ;;
        esac
        fail "$file:$line: '$target' does not resolve to a file"
        continue
      fi
    else
      resolved="$file"
    fi
    if [ -n "$anchor" ]; then
      case "$resolved" in
        *.md) ;;
        *) fail "$file:$line: '#$anchor' names a file that is not Markdown"; continue ;;
      esac
      if ! anchors "$resolved" | grep -qx -- "$anchor"; then
        fail "$file:$line: '#$anchor' is not a heading in $resolved"
      fi
    fi
  done < <(targets "$file")
}

files=0
for file in README.md docs/*.md; do
  [ -f "$file" ] || continue
  files=$((files + 1))
  check_file "$file"
done

# The index: toc.json and docs/README.md name the same pages in the same order.
# toc.json is read by its "slug" and "title" tokens in order, one page's slug
# followed by that page's title; a section's title has no slug before it.
toc_pairs="$(grep -o '"\(slug\|title\)"[[:space:]]*:[[:space:]]*"[^"]*"' docs/toc.json \
  | sed 's/^"\([a-z]*\)"[[:space:]]*:[[:space:]]*"\(.*\)"$/\1\t\2/' \
  | awk -F '\t' '$1 == "slug" { slug = $2; next } $1 == "title" && slug != "" { print slug "\t" $2; slug = "" }')"
toc_slugs="$(printf '%s\n' "$toc_pairs" | cut -f 1)"
index_slugs="$(unfenced docs/README.md | sed -n 's/^[0-9]*\t- \[[^]]*\](\([A-Za-z0-9_.-]*\)\.md).*$/\1/p')"

if [ -z "$toc_slugs" ]; then
  fail "docs/toc.json: no pages found"
elif [ "$toc_slugs" != "$index_slugs" ]; then
  fail "docs/toc.json and docs/README.md do not list the same pages in the same order:"
  diff <(printf '%s\n' "$toc_slugs") <(printf '%s\n' "$index_slugs") | sed 's/^/      /'
fi

while IFS=$'\t' read -r slug title; do
  [ -n "$slug" ] || continue
  if [ ! -f "docs/$slug.md" ]; then
    if allowed "$slug"; then echo "note  docs/$slug.md is not written yet (allowed)"; continue; fi
    fail "docs/toc.json: '$slug' has no docs/$slug.md"
    continue
  fi
  heading="$(first_heading "docs/$slug.md")"
  if [ "$heading" != "$title" ]; then
    fail "docs/$slug.md: its first heading is '$heading' but docs/toc.json says '$title'"
  fi
done <<EOF
$toc_pairs
EOF

echo "checked $files file(s), $links link(s); failures: $failures"
[ "$failures" -eq 0 ] || exit 1
