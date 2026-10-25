using System;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Opt-in (<see cref="EngineSettings.RejectInvalidLocalTime"/>): throw before a document is written if it holds a
        /// local time skipped by daylight saving, which ToUniversalTime would silently map onto the following hour (#2357)
        /// </summary>
        private void RejectInvalidLocalTime(BsonDocument doc)
        {
            if (_settings.RejectInvalidLocalTime == false) return;

            RejectInvalidLocalTime(doc, _settings.LocalTimeZone ?? TimeZoneInfo.Local);
        }

        private static void RejectInvalidLocalTime(BsonValue value, TimeZoneInfo zone)
        {
            if (value.IsDocument)
            {
                foreach (var item in value.AsDocument.Values)
                {
                    RejectInvalidLocalTime(item, zone);
                }
            }
            else if (value.IsArray)
            {
                foreach (var item in value.AsArray)
                {
                    RejectInvalidLocalTime(item, zone);
                }
            }
            else if (value.IsDateTime)
            {
                var date = value.AsDateTime;

                // Min/Max are stored without conversion; Unspecified kind makes the zone judge the wall clock as its own
                if (date.Kind != DateTimeKind.Utc && date != DateTime.MinValue && date != DateTime.MaxValue &&
                    zone.IsInvalidTime(DateTime.SpecifyKind(date, DateTimeKind.Unspecified)))
                {
                    throw new ArgumentException("Invalid local time cannot be stored as a UTC DateTime. Supply a valid local time or an explicit UTC value.", nameof(value));
                }
            }
        }
    }
}
