using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represents a 12-byte BSON ObjectId value used for document identifiers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ObjectId is a globally unique identifier composed of:
    /// </para>
    /// <list type="bullet">
    /// <item><description>4-byte timestamp (seconds since Unix epoch)</description></item>
    /// <item><description>3-byte machine identifier</description></item>
    /// <item><description>2-byte process identifier</description></item>
    /// <item><description>3-byte counter (incremented for each new ObjectId)</description></item>
    /// </list>
    /// <para>
    /// ObjectIds are sortable by creation time and are guaranteed to be unique within a single process.
    /// </para>
    /// </remarks>
    public class ObjectId : IComparable<ObjectId>, IEquatable<ObjectId>
    {
        /// <summary>
        /// Gets a zero-valued 12-byte ObjectId representing an empty identifier.
        /// </summary>
        public static ObjectId Empty => new ObjectId();

        #region Properties

        /// <summary>
        /// Gets the timestamp component representing seconds since the Unix epoch.
        /// </summary>
        public int Timestamp { get; }

        /// <summary>
        /// Gets the machine identifier component.
        /// </summary>
        public int Machine { get; }

        /// <summary>
        /// Gets the process identifier component.
        /// </summary>
        public short Pid { get; }

        /// <summary>
        /// Gets the increment counter component.
        /// </summary>
        public int Increment { get; }

        /// <summary>
        /// Gets the creation time of this ObjectId as a <see cref="DateTime"/>.
        /// </summary>
        public DateTime CreationTime
        {
            get { return BsonValue.UnixEpoch.AddSeconds(this.Timestamp); }
        }

        #endregion

        #region Ctor

        /// <summary>
        /// Initializes a new empty instance of the <see cref="ObjectId"/> class with all components set to zero.
        /// </summary>
        public ObjectId()
        {
            this.Timestamp = 0;
            this.Machine = 0;
            this.Pid = 0;
            this.Increment = 0;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectId"/> class with the specified component values.
        /// </summary>
        /// <param name="timestamp">The timestamp component (seconds since Unix epoch).</param>
        /// <param name="machine">The machine identifier component.</param>
        /// <param name="pid">The process identifier component.</param>
        /// <param name="increment">The increment counter component.</param>
        public ObjectId(int timestamp, int machine, short pid, int increment)
        {
            this.Timestamp = timestamp;
            this.Machine = machine;
            this.Pid = pid;
            this.Increment = increment;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectId"/> class by copying values from another ObjectId.
        /// </summary>
        /// <param name="from">The ObjectId to copy values from.</param>
        public ObjectId(ObjectId from)
        {
            this.Timestamp = from.Timestamp;
            this.Machine = from.Machine;
            this.Pid = from.Pid;
            this.Increment = from.Increment;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectId"/> class from a 24-character hexadecimal string.
        /// </summary>
        /// <param name="value">The 24-character hexadecimal string representation of an ObjectId.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not exactly 24 characters.</exception>
        public ObjectId(string value)
            : this(FromHex(value))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectId"/> class from a byte array.
        /// </summary>
        /// <param name="bytes">The byte array containing the 12-byte ObjectId representation.</param>
        /// <param name="startIndex">The zero-based starting position within the array. Default is 0.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bytes"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="bytes"/> does not contain at least <c>startIndex + 12</c> bytes.
        /// </exception>
        public ObjectId(byte[] bytes, int startIndex = 0)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length < startIndex + 12)
                throw new ArgumentException($"The byte array must contain at least {startIndex + 12} bytes.", nameof(bytes));

            this.Timestamp = 
                (bytes[startIndex + 0] << 24) + 
                (bytes[startIndex + 1] << 16) + 
                (bytes[startIndex + 2] << 8) + 
                bytes[startIndex + 3];

            this.Machine = 
                (bytes[startIndex + 4] << 16) + 
                (bytes[startIndex + 5] << 8) + 
                bytes[startIndex + 6];

            this.Pid = (short)
                ((bytes[startIndex + 7] << 8) + 
                bytes[startIndex + 8]);

            this.Increment = 
                (bytes[startIndex + 9] << 16) + 
                (bytes[startIndex + 10] << 8) + 
                bytes[startIndex + 11];
        }

        /// <summary>
        /// Converts a hexadecimal string to a byte array.
        /// </summary>
        private static byte[] FromHex(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(value));
            if (value.Length != 24) throw new ArgumentException(string.Format("ObjectId strings should be 24 hex characters, got {0} : \"{1}\"", value.Length, value));

            var bytes = new byte[12];

            for (var i = 0; i < 24; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(value.Substring(i, 2), 16);
            }

            return bytes;
        }

        #endregion

        #region Equals/CompareTo/ToString

        /// <summary>
        /// Determines whether this ObjectId is equal to another ObjectId.
        /// </summary>
        /// <param name="other">The ObjectId to compare with this instance.</param>
        /// <returns><see langword="true"/> if the ObjectIds are equal; otherwise, <see langword="false"/>.</returns>
        public bool Equals(ObjectId other)
        {
            return other != null && 
                this.Timestamp == other.Timestamp &&
                this.Machine == other.Machine &&
                this.Pid == other.Pid &&
                this.Increment == other.Increment;
        }

        /// <summary>
        /// Determines whether the specified object is equal to this ObjectId.
        /// </summary>
        /// <param name="other">The object to compare with this instance.</param>
        /// <returns><see langword="true"/> if the object is an ObjectId and is equal to this instance; otherwise, <see langword="false"/>.</returns>
        public override bool Equals(object other)
        {
            return Equals(other as ObjectId);
        }

        /// <summary>
        /// Returns a hash code for this ObjectId.
        /// </summary>
        /// <returns>A hash code for this instance.</returns>
        public override int GetHashCode()
        {
            int hash = 17;
            hash = 37 * hash + this.Timestamp.GetHashCode();
            hash = 37 * hash + this.Machine.GetHashCode();
            hash = 37 * hash + this.Pid.GetHashCode();
            hash = 37 * hash + this.Increment.GetHashCode();
            return hash;
        }

        /// <summary>
        /// Compares this ObjectId to another ObjectId.
        /// </summary>
        /// <param name="other">The ObjectId to compare with this instance.</param>
        /// <returns>
        /// A value less than zero if this instance is less than <paramref name="other"/>;
        /// zero if they are equal; or greater than zero if this instance is greater than <paramref name="other"/>.
        /// </returns>
        /// <remarks>
        /// Comparison is performed sequentially by Timestamp, Machine, Pid, and Increment components.
        /// </remarks>
        public int CompareTo(ObjectId other)
        {
            var r = this.Timestamp.CompareTo(other.Timestamp);
            if (r != 0) return r;

            r = this.Machine.CompareTo(other.Machine);
            if (r != 0) return r;

            r = this.Pid.CompareTo(other.Pid);
            if (r != 0) return r < 0 ? -1 : 1;

            return this.Increment.CompareTo(other.Increment);
        }

        /// <summary>
        /// Writes this ObjectId as a 12-byte array to the specified byte array starting at the given index.
        /// </summary>
        /// <param name="bytes">The destination byte array.</param>
        /// <param name="startIndex">The zero-based starting position in the destination array.</param>
        public void ToByteArray(byte[] bytes, int startIndex)
        {
            bytes[startIndex + 0] = (byte)(this.Timestamp >> 24);
            bytes[startIndex + 1] = (byte)(this.Timestamp >> 16);
            bytes[startIndex + 2] = (byte)(this.Timestamp >> 8);
            bytes[startIndex + 3] = (byte)(this.Timestamp);
            bytes[startIndex + 4] = (byte)(this.Machine >> 16);
            bytes[startIndex + 5] = (byte)(this.Machine >> 8);
            bytes[startIndex + 6] = (byte)(this.Machine);
            bytes[startIndex + 7] = (byte)(this.Pid >> 8);
            bytes[startIndex + 8] = (byte)(this.Pid);
            bytes[startIndex + 9] = (byte)(this.Increment >> 16);
            bytes[startIndex + 10] = (byte)(this.Increment >> 8);
            bytes[startIndex + 11] = (byte)(this.Increment);
        }

        /// <summary>
        /// Converts this ObjectId to a 12-byte array.
        /// </summary>
        /// <returns>A byte array containing the 12-byte representation of this ObjectId.</returns>
        public byte[] ToByteArray()
        {
            var bytes = new byte[12];

            this.ToByteArray(bytes, 0);

            return bytes;
        }

        /// <summary>
        /// Returns a 24-character lowercase hexadecimal string representation of this ObjectId.
        /// </summary>
        /// <returns>A 24-character hexadecimal string.</returns>
        public override string ToString()
        {
            return BitConverter.ToString(this.ToByteArray()).Replace("-", "").ToLower();
        }

        #endregion

        #region Operators

        /// <summary>
        /// Determines whether two ObjectId instances are equal.
        /// </summary>
        /// <param name="lhs">The first ObjectId to compare.</param>
        /// <param name="rhs">The second ObjectId to compare.</param>
        /// <returns><see langword="true"/> if the ObjectIds are equal; otherwise, <see langword="false"/>.</returns>
        public static bool operator ==(ObjectId lhs, ObjectId rhs)
        {
            if (lhs is null) return rhs is null;
            if (rhs is null) return false; // don't check type because sometimes different types can be ==

            return lhs.Equals(rhs);
        }

        /// <summary>
        /// Determines whether two ObjectId instances are not equal.
        /// </summary>
        /// <param name="lhs">The first ObjectId to compare.</param>
        /// <param name="rhs">The second ObjectId to compare.</param>
        /// <returns><see langword="true"/> if the ObjectIds are not equal; otherwise, <see langword="false"/>.</returns>
        public static bool operator !=(ObjectId lhs, ObjectId rhs)
        {
            return !(lhs == rhs);
        }

        /// <summary>
        /// Determines whether one ObjectId is greater than or equal to another.
        /// </summary>
        /// <param name="lhs">The first ObjectId to compare.</param>
        /// <param name="rhs">The second ObjectId to compare.</param>
        /// <returns><see langword="true"/> if <paramref name="lhs"/> is greater than or equal to <paramref name="rhs"/>; otherwise, <see langword="false"/>.</returns>
        public static bool operator >=(ObjectId lhs, ObjectId rhs)
        {
            return lhs.CompareTo(rhs) >= 0;
        }

        /// <summary>
        /// Determines whether one ObjectId is greater than another.
        /// </summary>
        /// <param name="lhs">The first ObjectId to compare.</param>
        /// <param name="rhs">The second ObjectId to compare.</param>
        /// <returns><see langword="true"/> if <paramref name="lhs"/> is greater than <paramref name="rhs"/>; otherwise, <see langword="false"/>.</returns>
        public static bool operator >(ObjectId lhs, ObjectId rhs)
        {
            return lhs.CompareTo(rhs) > 0;
        }

        /// <summary>
        /// Determines whether one ObjectId is less than another.
        /// </summary>
        /// <param name="lhs">The first ObjectId to compare.</param>
        /// <param name="rhs">The second ObjectId to compare.</param>
        /// <returns><see langword="true"/> if <paramref name="lhs"/> is less than <paramref name="rhs"/>; otherwise, <see langword="false"/>.</returns>
        public static bool operator <(ObjectId lhs, ObjectId rhs)
        {
            return lhs.CompareTo(rhs) < 0;
        }

        /// <summary>
        /// Determines whether one ObjectId is less than or equal to another.
        /// </summary>
        /// <param name="lhs">The first ObjectId to compare.</param>
        /// <param name="rhs">The second ObjectId to compare.</param>
        /// <returns><see langword="true"/> if <paramref name="lhs"/> is less than or equal to <paramref name="rhs"/>; otherwise, <see langword="false"/>.</returns>
        public static bool operator <=(ObjectId lhs, ObjectId rhs)
        {
            return lhs.CompareTo(rhs) <= 0;
        }

        #endregion

        #region Static methods

        private static readonly int _machine;
        private static readonly short _pid;
        private static int _increment;

        // static constructor
        static ObjectId()
        {
            _machine = (GetMachineHash() +
#if HAVE_APP_DOMAIN
                AppDomain.CurrentDomain.Id
#else
                10000 // Magic number
#endif   
                ) & 0x00ffffff;
            _increment = (new Random()).Next();

            try
            {
                _pid = (short)GetCurrentProcessId();
            }
            catch (SecurityException)
            {
                _pid = 0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int GetCurrentProcessId()
        {
#if HAVE_PROCESS
            return Process.GetCurrentProcess().Id;
#else
            return (new Random()).Next(0, 5000); // Any same number for this process
#endif
        }

        private static int GetMachineHash()
        {
            var hostName =
#if HAVE_ENVIRONMENT
                Environment.MachineName; // use instead of Dns.HostName so it will work offline
#else
                "SOMENAME";
#endif
            return 0x00ffffff & hostName.GetHashCode(); // use first 3 bytes of hash
        }

        /// <summary>
        /// Creates a new globally unique ObjectId.
        /// </summary>
        /// <returns>A new <see cref="ObjectId"/> with the current timestamp, machine identifier, process identifier, and an incremented counter.</returns>
        /// <remarks>
        /// This method is thread-safe and generates ObjectIds that are sortable by creation time.
        /// </remarks>
        public static ObjectId NewObjectId()
        {
            var timestamp = (long)Math.Floor((DateTime.UtcNow - BsonValue.UnixEpoch).TotalSeconds);
            var inc = Interlocked.Increment(ref _increment) & 0x00ffffff;

            return new ObjectId((int)timestamp, _machine, _pid, inc);
        }

        #endregion
    }
}