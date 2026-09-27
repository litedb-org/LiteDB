namespace LiteDB.Engine
{
    internal partial class BufferWriter
    {
        private void WriteElementKey(string key, int arrayIndex)
        {
            if (arrayIndex < 0)
            {
                this.WriteCString(key);
                return;
            }

            var divisor = 1;

            while (divisor <= arrayIndex / 10)
            {
                divisor *= 10;
            }

            do
            {
                this.Write((byte)('0' + arrayIndex / divisor));
                arrayIndex %= divisor;
                divisor /= 10;
            }
            while (divisor > 0);

            this.Write((byte)0x00);
        }
    }
}
