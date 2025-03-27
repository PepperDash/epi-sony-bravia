using System;
using System.Collections.Generic;

namespace Pepperdash.Essentials.Plugins.SonyBravia
{
    public static class SimpleIpParsingUtils
    {
        public static IEnumerable<string> SplitInParts(this string s, int partLength)
        {
            if ((s == null) || (partLength <= 0)) yield break;
            for (var i = 0; i < s.Length; i += partLength)
                yield return s.Substring(i, Math.Min(partLength, s.Length - i));
        }
    }
}