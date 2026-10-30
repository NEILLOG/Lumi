namespace Lumi.Core.Usage;

/// <summary>Jaro-Winkler 相似度（前綴加權 0.1、最多看 4 個字元）。比較前都轉成小寫。</summary>
public static class JaroWinkler
{
    private const double PrefixScale = 0.1;
    private const int MaxPrefix = 4;

    /// <returns>0 到 1，1 表示相同。</returns>
    public static double Similarity(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        if (a.Length == 0 && b.Length == 0)
            return 1.0;
        if (a.Length == 0 || b.Length == 0)
            return 0.0;

        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;

        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - window);
            var end = Math.Min(i + window + 1, b.Length);
            for (var j = start; j < end; j++)
            {
                if (bMatched[j] || a[i] != b[j])
                    continue;
                aMatched[i] = true;
                bMatched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
            return 0.0;

        // 兩邊相符字元的順序不一樣的個數（半數即轉置數）
        var outOfOrder = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i])
                continue;
            while (!bMatched[k])
                k++;
            if (a[i] != b[k])
                outOfOrder++;
            k++;
        }
        var transpositions = outOfOrder / 2.0;

        var jaro = ((double)matches / a.Length + (double)matches / b.Length + (matches - transpositions) / matches) / 3.0;

        var prefix = 0;
        while (prefix < Math.Min(MaxPrefix, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix])
            prefix++;

        return jaro + prefix * PrefixScale * (1 - jaro);
    }
}
