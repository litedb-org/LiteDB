using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Implement a generic result structure with value and exception. This value can be partial value (like BsonDocument/Array)
    /// </summary>
    internal struct Result<T>
        where T : class
    {
        public T Value;
        public Exception Exception;

        public bool Ok => this.Exception == null;
        public bool Fail => this.Exception != null;

        /// <summary>
        /// Get array result or throw exception if there is any error on read result
        /// </summary>
        public T GetValue() => this.Ok ? this.Value : throw this.Exception;

        public Result(T value, Exception ex = null)
        {
            this.Value = value;
            this.Exception = ex;
        }


        public static implicit operator T(Result<T> value)
        {
            return value.Value;
        }

        public static implicit operator Result<T>(T value)
        {
            return new Result<T>(value, null);
        }
    }
}