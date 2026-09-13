using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace LiteDB.Stress
{
    public class TimeSpanEx
    {
        public static TimeSpan Parse(string duration)
        {
            var re = new Regex(@"(?<num>\d+)\s*(?<unit>ms|s|m|h)?");

            var match = re.Match(duration);

            if (match.Success)
            {
                var num = double.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture.NumberFormat);
                var unit = match.Groups["unit"].Value.ToLower();

                switch(unit)
                {
                    case "": 
                    case "ms": return TimeSpan.FromMilliseconds(num);
                    case "s": return TimeSpan.FromSeconds(num);
                    case "m": return TimeSpan.FromMinutes(num);
                    case "h": return TimeSpan.FromHours(num);
                }
            }

            throw new ArgumentException("Duration must be in format: number + unit (ms, s, m, h)");
        }
    }
}
