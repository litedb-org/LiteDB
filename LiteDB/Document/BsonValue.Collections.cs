using System;
using System.Diagnostics;

namespace LiteDB
{
    public partial class BsonValue
    {
        #region Index "this" property

        /// <summary>
        /// Get/Set a field for document. Fields are case sensitive - Works only when value are document
        /// </summary>
        public virtual BsonValue this[string name]
        {
            get
            {
                if (this.IsDocument) return this.AsDocument[name];

                throw new InvalidOperationException("Cannot access non-document type value on " + this.RawValue);
            }
            set
            {
                if (this.IsDocument)
                {
                    this.AsDocument[name] = value;
                    return;
                }

                throw new InvalidOperationException("Cannot access non-document type value on " + this.RawValue);
            }
        }

        /// <summary>
        /// Get/Set value in array position. Works only when value are array
        /// </summary>
        public virtual BsonValue this[int index]
        {
            get
            {
                if (this.IsArray) return this.AsArray[index];

                throw new InvalidOperationException("Cannot access non-array type value on " + this.RawValue);
            }
            set
            {
                if (this.IsArray)
                {
                    this.AsArray[index] = value;
                    return;
                }

                throw new InvalidOperationException("Cannot access non-array type value on " + this.RawValue);
            }
        }

        #endregion

        #region Convert collection types

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public BsonArray AsArray => this is BsonArray array
            ? array
            : this.IsArray ? this.RawValue as BsonArray : null;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public BsonDocument AsDocument => this is BsonDocument document
            ? document
            : this.IsDocument ? this.RawValue as BsonDocument : null;

        #endregion
    }
}
