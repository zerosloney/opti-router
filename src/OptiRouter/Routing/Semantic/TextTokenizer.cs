namespace OptiRouter.Routing;

/// <summary>
/// 评测相似度与学习特征共享的文本分词：分隔符切段 + CJK 连续游程字符 bigram。
/// 中文无词边界，整段单 token 会使任意两句相似度≈0、词袋特征全部塌缩，
/// 故 CJK 段按字符 bigram 切分（与 OfflineEvalRunner 既有口径一致，抽取共享避免两份漂移）。
/// </summary>
internal static class TextTokenizer
{
    private static readonly char[] Separators =
        { ' ', '\t', '\r', '\n', ',', '.', '，', '。', '！', '？', ':', '：', '\'', '"', '-' };

    public static IEnumerable<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (var seg in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            int i = 0;
            while (i < seg.Length)
            {
                bool cjk = IsCjk(seg[i]);
                int start = i;
                while (i < seg.Length && IsCjk(seg[i]) == cjk) i++;
                int len = i - start;
                if (cjk && len >= 2)
                {
                    for (int k = 0; k < len - 1; k++)
                        tokens.Add(seg.Substring(start + k, 2));
                }
                else
                {
                    tokens.Add(seg.Substring(start, len));
                }
            }
        }
        return tokens;
    }

    public static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) ||  // CJK 统一表意文字
        (ch >= 0x3040 && ch <= 0x30FF) ||  // 平假名/片假名
        (ch >= 0xAC00 && ch <= 0xD7AF);    // 韩文音节
}
