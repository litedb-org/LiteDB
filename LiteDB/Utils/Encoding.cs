using System.Text;

namespace LiteDB
{
    internal class StringEncoding
    {
        // Original Encoding.UTF8 will replace unpaired surrogate with U+FFFD, which is not suitable for database
        // so, we need to use new UTF8Encoding(false, true) to make throw exception when unpaired surrogate is found
        //public static System.Text.Encoding UTF8 = new UTF8Encoding(false, true);
        public static Encoding UTF8 = new UTF8Encoding(false, true);
    }
}
