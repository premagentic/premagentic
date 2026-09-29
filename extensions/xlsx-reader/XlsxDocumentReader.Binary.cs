using System.Buffers.Binary;
using System.Text;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Extensions.Shared;

namespace Premagentic.Extensions.Xlsx;

/// <summary>
/// The binary workbook (.xlsb): the same zip, the same XML relationships and
/// the same limits as a .xlsx, with the workbook, its sheets, its shared
/// strings and its styles written as Excel's binary records (MS-XLSB) instead
/// of XML. A record part is flat, one record after another, so there is no
/// depth for a limit to hold; the XML parts beside it keep the depth limit.
/// </summary>
public sealed partial class XlsxDocumentReader
{
    // Record types, from MS-XLSB section 2.3.
    private const int BrtRowHdr = 0;
    private const int BrtCellBlank = 1;
    private const int BrtCellRk = 2;
    private const int BrtCellError = 3;
    private const int BrtCellBool = 4;
    private const int BrtCellReal = 5;
    private const int BrtCellSt = 6;
    private const int BrtCellIsst = 7;
    private const int BrtFmlaString = 8;
    private const int BrtFmlaNum = 9;
    private const int BrtFmlaBool = 10;
    private const int BrtFmlaError = 11;
    private const int BrtSstItem = 19;
    private const int BrtFmt = 44;
    private const int BrtXf = 47;
    private const int BrtBeginSheet = 129;
    private const int BrtBeginBook = 131;
    private const int BrtEndSheetData = 146;
    private const int BrtWbProp = 153;
    private const int BrtBundleSh = 156;
    private const int BrtBeginSst = 159;
    private const int BrtBeginStyleSheet = 278;
    private const int BrtBeginCellXFs = 617;
    private const int BrtEndCellXFs = 618;

    private static ReadDocument ReadBinary(ZipParts parts, string main, string fileName, XlsxReaderLimits limits, ReadClock clock)
    {
        var relationships = parts.Relationships(main);
        if (HoldsMacroSheet(relationships)) return ReadDocument.Skipped("macro-enabled");

        var book = Records.Open(parts, main, BrtBeginBook, clock) ?? throw new UnreadableDocumentException(Damaged);
        var date1904 = false;
        var sheets = new List<(string? RelId, string Name)>();
        var listed = 0;
        while (book.Next())
        {
            if (book.Type == BrtWbProp) date1904 = (book.U32(0) & 1) != 0;
            else if (book.Type == BrtBundleSh)
            {
                // Each one counted, so the refusal says how many, but only
                // as many kept as would be read.
                if (++listed > limits.MaxSheets) continue;
                var at = 8;
                var relId = book.NullableString(ref at);
                sheets.Add((relId, book.String(ref at)));
            }
        }
        if (listed > limits.MaxSheets)
            throw new UnreadableDocumentException($"holds {listed} sheets, more than the {limits.MaxSheets} this reader reads");

        var byId = new Dictionary<string, ZipParts.Relationship>(StringComparer.Ordinal);
        foreach (var r in relationships) byId.TryAdd(r.Id, r);

        var cells = new Cells(limits.MaxCells, clock);
        var values = new Values(
            BinarySharedStrings(parts, main, relationships, cells, clock),
            BinaryStyles(parts, main, relationships, clock),
            date1904);
        var writer = new Writer(limits.MaxTextChars);

        for (var i = 0; i < sheets.Count; i++)
        {
            var (relId, sheetName) = sheets[i];
            // A sheet with no relationship has no part: a module sheet, which
            // holds no cells.
            if (relId is null) continue;
            if (!byId.TryGetValue(relId, out var target) || target.External) throw new UnreadableDocumentException(Damaged);
            // A chart sheet or a dialog sheet holds no cells.
            if (!target.Type.EndsWith("/worksheet", StringComparison.Ordinal)) continue;

            var name = Clean(sheetName) is { Length: > 0 } n ? n : $"Sheet {i + 1}";
            var sheet = Records.Open(parts, parts.Resolve(main, target.Target), BrtBeginSheet, clock)
                        ?? throw new UnreadableDocumentException(Damaged);
            BinarySheet(sheet, writer, cells, values, heading: () =>
            {
                if (writer.IsEmpty) writer.Heading(1, fileName);
                writer.Heading(2, name);
            });
        }

        var title = Title(parts.Xml("docProps/core.xml"));
        return writer.IsEmpty ? new ReadDocument("", title, null) : new ReadDocument(writer.Text, title, null);
    }

    /// <summary>
    /// One sheet's rows, as the .xlsx path reads them: each row with any text
    /// is a paragraph, and the sheet's heading is written before its first
    /// one. Only rows and cells are looked at, and nothing after the end of
    /// the sheet's data is read.
    /// </summary>
    private static void BinarySheet(Records sheet, Writer writer, Cells cells, Values values, Action heading)
    {
        var headed = false;
        var texts = new List<string>();

        void EndRow()
        {
            var line = string.Join('\t', texts);
            texts.Clear();
            if (line.Length == 0) return;
            if (!headed)
            {
                heading();
                headed = true;
            }
            writer.Row(line);
        }

        while (sheet.Next())
        {
            switch (sheet.Type)
            {
                case BrtEndSheetData:
                    EndRow();
                    return;
                case BrtRowHdr:
                    EndRow();
                    cells.Row();
                    break;
                case >= BrtCellBlank and <= BrtFmlaError:
                    cells.Count();
                    var text = BinaryCell(sheet, values);
                    if (text.Length > 0) texts.Add(text);
                    break;
            }
        }
        EndRow();
    }

    /// <summary>
    /// One cell record's saved value as text. Every cell record begins with
    /// the same eight bytes, its column and then its style in the low three
    /// bytes of the next four; the value follows. A formula's saved value is
    /// the text and the formula after it is never read.
    /// </summary>
    private static string BinaryCell(Records cell, Values values)
    {
        var style = (int)(cell.U32(4) & 0xFFFFFF);
        switch (cell.Type)
        {
            case BrtCellRk:
                return values.Number(Rk(cell.U32(8)), style);
            case BrtCellReal or BrtFmlaNum:
                return values.Number(cell.F64(8), style);
            case BrtCellBool or BrtFmlaBool:
                return cell.U8(8) switch { 1 => "TRUE", 0 => "FALSE", _ => "" };
            case BrtCellError or BrtFmlaError:
                return ErrorText(cell.U8(8));
            case BrtCellSt or BrtFmlaString:
                return Clean(cell.String(8));
            case BrtCellIsst:
                var index = cell.U32(8);
                return index < (uint)values.Shared.Count
                    ? values.Shared[(int)index]
                    : throw new UnreadableDocumentException($"holds a cell that names shared string {index}, past the {values.Shared.Count} its table holds");
            default:
                return "";
        }
    }

    /// <summary>
    /// An RK number: its lowest bit says the value was stored times 100, the
    /// next that the other 30 bits are a signed integer rather than the top 30
    /// bits of a double whose other 34 are zero.
    /// </summary>
    private static double Rk(uint rk)
    {
        double value = (rk & 2) != 0 ? (int)rk >> 2 : BitConverter.UInt64BitsToDouble((ulong)(rk & 0xFFFFFFFC) << 32);
        return (rk & 1) != 0 ? value / 100 : value;
    }

    /// <summary>An error as Excel writes it, from its code; empty for a code the format does not have.</summary>
    private static string ErrorText(byte code) => code switch
    {
        0x00 => "#NULL!",
        0x07 => "#DIV/0!",
        0x0F => "#VALUE!",
        0x17 => "#REF!",
        0x1D => "#NAME?",
        0x24 => "#NUM!",
        0x2A => "#N/A",
        0x2B => "#GETTING_DATA",
        _ => "",
    };

    /// <summary>
    /// The shared string table, each entry held to the cell limit; empty when
    /// the workbook has none. An entry is a flags byte and then its text; the
    /// runs and the phonetic guide after the text are not read.
    /// </summary>
    private static List<string> BinarySharedStrings(ZipParts parts, string main, IReadOnlyList<ZipParts.Relationship> relationships, Cells cells, ReadClock clock)
    {
        var strings = new List<string>();
        var target = relationships.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal) && !r.External);
        var table = target is null ? null : Records.Open(parts, parts.Resolve(main, target.Target), BrtBeginSst, clock);
        if (table is null) return strings;

        while (table.Next())
        {
            if (table.Type != BrtSstItem) continue;
            if (strings.Count >= cells.Max)
                throw new UnreadableDocumentException($"holds more than {cells.Max} shared strings, more than the cells this reader reads");
            strings.Add(Clean(table.String(1)));
        }
        return strings;
    }

    /// <summary>
    /// What each cell style says about a number. A cell's style counts among
    /// the cell formats only; the cell style formats written before them are
    /// another list.
    /// </summary>
    private static Shown[] BinaryStyles(ZipParts parts, string main, IReadOnlyList<ZipParts.Relationship> relationships, ReadClock clock)
    {
        var target = relationships.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal) && !r.External);
        var styles = target is null ? null : Records.Open(parts, parts.Resolve(main, target.Target), BrtBeginStyleSheet, clock);
        if (styles is null) return [];

        var custom = new Dictionary<int, string>();
        var formats = new List<int>();
        var inCellXfs = false;
        while (styles.Next())
        {
            switch (styles.Type)
            {
                case BrtFmt:
                    custom[styles.U16(0)] = styles.String(2);
                    break;
                case BrtBeginCellXFs:
                    inCellXfs = true;
                    break;
                case BrtEndCellXFs:
                    inCellXfs = false;
                    break;
                case BrtXf when inCellXfs:
                    formats.Add(styles.U16(2));
                    break;
            }
        }
        return formats.Select(id => custom.TryGetValue(id, out var code) ? ShownBy(code) : BuiltIn(id)).ToArray();
    }

    /// <summary>
    /// One binary part's records, read one after another. A record is its
    /// type, one or two bytes, and its size, one to four, each seven bits a
    /// byte with the low bits first and a byte's high bit saying another
    /// follows; then its body of that size. A record that runs past the end of
    /// its part refuses the file; a field read past the end of its record is
    /// damage.
    /// </summary>
    private sealed class Records
    {
        private readonly byte[] _data;
        private readonly string _name;
        private readonly ReadClock _clock;
        private int _at;
        private int _start;
        private int _length;
        private int _count;

        private Records(byte[] data, string name, ReadClock clock)
        {
            _data = data;
            _name = name;
            _clock = clock;
        }

        /// <summary>The type of the record <see cref="Next"/> moved to.</summary>
        public int Type { get; private set; }

        /// <summary>
        /// The records of the part named <paramref name="name"/>, past its
        /// first, or null when the file has no such part. A part that does not
        /// begin with the record its kind must begin with is not that part.
        /// </summary>
        public static Records? Open(ZipParts parts, string name, int first, ReadClock clock)
        {
            var data = parts.Bytes(name);
            if (data is null) return null;
            var records = new Records(data, name, clock);
            if (!records.Next() || records.Type != first) throw new UnreadableDocumentException(Damaged);
            return records;
        }

        /// <summary>Moves to the next record; false at the end of the part.</summary>
        public bool Next()
        {
            if (_at >= _data.Length) return false;
            if (++_count % 4096 == 0) _clock.Check();

            int b = Byte();
            var type = b & 0x7F;
            if ((b & 0x80) != 0) type |= (Byte() & 0x7F) << 7;
            var size = 0;
            for (var i = 0; i < 4; i++)
            {
                b = Byte();
                size |= (b & 0x7F) << (7 * i);
                if ((b & 0x80) == 0) break;
            }
            if (size > _data.Length - _at) throw PastTheEnd();

            Type = type;
            _start = _at;
            _length = size;
            _at += size;
            return true;
        }

        private byte Byte() => _at < _data.Length ? _data[_at++] : throw PastTheEnd();

        private UnreadableDocumentException PastTheEnd() => new($"the part {_name} holds a record that runs past its end");

        private ReadOnlySpan<byte> Field(int at, int size) =>
            at >= 0 && size <= _length - at ? _data.AsSpan(_start + at, size) : throw new UnreadableDocumentException(Damaged);

        public byte U8(int at) => Field(at, 1)[0];

        public ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Field(at, 2));

        public uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Field(at, 4));

        public double F64(int at) => BinaryPrimitives.ReadDoubleLittleEndian(Field(at, 8));

        /// <summary>The string at <paramref name="at"/> in the record's body.</summary>
        public string String(int at) => String(ref at);

        /// <summary>The string at <paramref name="at"/>, which is moved past it.</summary>
        public string String(ref int at) => NullableString(ref at) ?? throw new UnreadableDocumentException(Damaged);

        /// <summary>
        /// A string as the format writes it, a count of UTF-16 code units and
        /// then the code units, or null where the count is all ones; a count
        /// past the end of the record is damage.
        /// </summary>
        public string? NullableString(ref int at)
        {
            var count = U32(at);
            at += 4;
            if (count == uint.MaxValue) return null;
            if (count > (uint)(_length - at) / 2) throw new UnreadableDocumentException(Damaged);
            var text = Encoding.Unicode.GetString(Field(at, (int)count * 2));
            at += (int)count * 2;
            return text;
        }
    }
}
