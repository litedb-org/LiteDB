namespace LiteDB.Engine
{
    /// <summary>
    /// Query-local reuse of short ASCII field names. Every hit compares all bytes;
    /// collisions and non-ASCII names use the ordinary decoder. No page is retained.
    /// </summary>
    internal sealed class BsonFieldNameCache
    {
        private readonly string[] _names = new string[32];

        internal string Read(byte[] bytes, int offset, int count)
        {
            if (count == 0) return string.Empty;
            if (count > 64) return StringEncoding.UTF8.GetString(bytes, offset, count);
            var slot = ((count << 3) ^ bytes[offset] ^ bytes[offset + count - 1]) & (_names.Length - 1);
            var name = _names[slot];
            if (name != null && name.Length == count)
            {
                var i = 0;
                while (i < count && name[i] == bytes[offset + i]) i++;
                if (i == count) return name;
            }

            name = StringEncoding.UTF8.GetString(bytes, offset, count);
            for (var i = 0; i < name.Length; i++)
                if (name[i] > 127) return name;
            _names[slot] = name;
            return name;
        }
    }
}
