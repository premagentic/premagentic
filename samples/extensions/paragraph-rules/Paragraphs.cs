namespace Premagentic.Samples.ParagraphRules;

/// <summary>Splits text into paragraphs, at every run of blank lines.</summary>
public static class Paragraphs
{
    public static IEnumerable<string> Of(string text)
    {
        var current = new List<string>();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                if (current.Count > 0) yield return string.Join('\n', current);
                current.Clear();
                continue;
            }
            current.Add(line.TrimEnd());
        }
        if (current.Count > 0) yield return string.Join('\n', current);
    }
}
