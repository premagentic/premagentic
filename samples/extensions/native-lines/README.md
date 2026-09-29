# native-lines: a sample extension with a native library

A chunker, `native-line`, that cuts a document into one chunk per line. It
finds each line's end by calling one C function, `prem_sample_line_end`, in a
native library the extension ships for each platform:

| Platform | File in the extension's folder |
|---|---|
| Windows x64 | `runtimes/win-x64/native/prem_sample_lines.dll` |
| Linux x64 | `runtimes/linux-x64/native/libprem_sample_lines.so` |

Building this project copies both files into place and writes
`extension.json`, whose `files` list names each one with its SHA-256. The host
loads a native library only when it is listed and its bytes match, and
measures it again just before loading it. A file under `runtimes/` that the
manifest does not list refuses the extension.

## The two native files are committed

The files in `native/win-x64/` and `native/linux-x64/` are built, and they are
in the repository so that building the solution needs no C compiler, here or
on a build server. They are reproducible from the one C file beside them,
`native/prem_sample_lines.c`, which uses no C library and links nothing:

```
scripts/build-native-sample.sh           # build both over the committed files
scripts/build-native-sample.sh --check   # build both elsewhere and compare byte for byte
```

The script needs clang and lld; the committed files were made with clang
22.1.8. `--check` prints `identical` for each file when the committed bytes
are what the source builds to, and exits 1 when either differs.
