namespace SwiftAid.Gateway.Ellai;

internal static class EllaiSpeechSentenceChunker
{
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var sentences = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '?' or '!'))
            {
                continue;
            }

            var next = index + 1;
            if (next < text.Length && !char.IsWhiteSpace(text[next]))
            {
                continue;
            }

            Add(text.AsSpan(start, next - start), sentences);
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }
            start = next;
            index = next - 1;
        }

        Add(text.AsSpan(start), sentences);
        return sentences;
    }

    private static void Add(ReadOnlySpan<char> value, ICollection<string> sentences)
    {
        var sentence = value.Trim();
        if (!sentence.IsEmpty)
        {
            sentences.Add(sentence.ToString());
        }
    }
}
