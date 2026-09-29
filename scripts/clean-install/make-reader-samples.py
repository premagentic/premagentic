#!/usr/bin/env python3
"""Writes the invented files the clean-install test reads through the PDF and
Word readers, into the folder named:

    python3 make-reader-samples.py <folder>

  loading-dock.pdf    two pages of text, the second answering "where do returns go"
  visitor-policy.docx a heading, a paragraph, a second-level heading and another
  old-form.docm       a name the Word reader skips as macro-enabled, never opened

Every word is invented. The files are generated each run, byte for byte the
same, so none is committed. Standard library only.
"""
import os
import sys
import zipfile

folder = sys.argv[1]
os.makedirs(folder, exist_ok=True)


def pdf(pages):
    """A PDF with one page of Helvetica lines per list, and a correct cross-reference table."""
    def esc(s):
        return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")

    objects = {
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: ("<< /Type /Pages /Kids [%s] /Count %d >>" % (
            " ".join("%d 0 R" % (10 + 2 * n) for n in range(len(pages))), len(pages))).encode(),
        3: b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    }
    for n, lines in enumerate(pages):
        content = "BT /F1 12 Tf 72 720 Td 16 TL\n" + "".join("(%s) Tj T*\n" % esc(l) for l in lines) + "ET\n"
        objects[10 + 2 * n] = ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                               "/Resources << /Font << /F1 3 0 R >> >> /Contents %d 0 R >>" % (11 + 2 * n)).encode()
        data = content.encode("latin-1")
        objects[11 + 2 * n] = b"<< /Length %d >>\nstream\n" % len(data) + data + b"\nendstream"

    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = {}
    for oid in sorted(objects):
        offsets[oid] = len(out)
        out += b"%d 0 obj\n" % oid + objects[oid] + b"\nendobj\n"
    size = max(objects) + 1
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % size
    for oid in range(1, size):
        out += (b"%010d 00000 n \n" % offsets[oid]) if oid in offsets else b"0000000000 65535 f \n"
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (size, xref)
    return bytes(out)


W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'


def docx(path, body):
    def para(text, style=None):
        props = '<w:pPr><w:pStyle w:val="%s"/></w:pPr>' % style if style else ""
        return '<w:p>%s<w:r><w:t xml:space="preserve">%s</w:t></w:r></w:p>' % (props, text)

    parts = {
        "[Content_Types].xml":
            '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Override PartName="/word/document.xml" '
            'ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>',
        "_rels/.rels":
            '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
            'Target="word/document.xml"/></Relationships>',
        "word/styles.xml":
            '<?xml version="1.0" encoding="UTF-8"?><w:styles %s>'
            '<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/></w:style>'
            '<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/></w:style></w:styles>' % W,
        "word/document.xml":
            '<?xml version="1.0" encoding="UTF-8"?><w:document %s><w:body>%s<w:sectPr/></w:body></w:document>'
            % (W, "".join(para(t, s) for s, t in body)),
    }
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in parts.items():
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, text.encode("utf-8"))


with open(os.path.join(folder, "loading-dock.pdf"), "wb") as f:
    f.write(pdf([
        ["Loading dock", "Deliveries sign in at the gate before seven in the morning."],
        ["Returns go to bay four with the pink slip attached."],
    ]))
docx(os.path.join(folder, "visitor-policy.docx"), [
    ("Heading1", "Visitors"),
    (None, "Visitors wear a yellow badge on the warehouse floor."),
    ("Heading2", "Escorts"),
    (None, "A visitor is escorted past the red line at all times."),
])
with open(os.path.join(folder, "old-form.docm"), "wb") as f:
    f.write(b"not opened: the name alone makes it macro-enabled\n")
print("wrote loading-dock.pdf, visitor-policy.docx and old-form.docm to " + folder)
