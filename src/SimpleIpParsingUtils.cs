using System;
using System.Collections.Generic;

namespace SonyBraviaEpi
{
    public static class SimpleIpParsingUtils
    {
        public static IEnumerable<string> SplitInParts(this String s, Int32 partLength)
        {
            if ((s == null) || (partLength <= 0)) yield break;
            for (var i = 0; i < s.Length; i += partLength)
                yield return s.Substring(i, Math.Min(partLength, s.Length - i));
        }
    }
}