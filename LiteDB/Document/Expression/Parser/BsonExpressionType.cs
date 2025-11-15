using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Specifies the type of operation or value in a <see cref="BsonExpression"/>.
    /// </summary>
    public enum BsonExpressionType : byte
    {
        /// <summary>
        /// Double-precision floating-point literal value.
        /// </summary>
        Double = 1,

        /// <summary>
        /// Integer literal value.
        /// </summary>
        Int = 2,

        /// <summary>
        /// String literal value.
        /// </summary>
        String = 3,

        /// <summary>
        /// Boolean literal value.
        /// </summary>
        Boolean = 4,

        /// <summary>
        /// Null literal value.
        /// </summary>
        Null = 5,

        /// <summary>
        /// Array literal or construction expression.
        /// </summary>
        Array = 6,

        /// <summary>
        /// Document literal or construction expression.
        /// </summary>
        Document = 7,

        /// <summary>
        /// Parameter reference (e.g., <c>@paramName</c> or <c>@0</c>).
        /// </summary>
        Parameter = 8,

        /// <summary>
        /// Method or function call expression.
        /// </summary>
        Call = 9,

        /// <summary>
        /// Document field path expression (e.g., <c>$.name</c> or <c>address.city</c>).
        /// </summary>
        Path = 10,

        /// <summary>
        /// Modulo arithmetic operation (<c>%</c>).
        /// </summary>
        Modulo = 11,

        /// <summary>
        /// Addition arithmetic operation (<c>+</c>).
        /// </summary>
        Add = 12,

        /// <summary>
        /// Subtraction arithmetic operation (<c>-</c>).
        /// </summary>
        Subtract = 13,

        /// <summary>
        /// Multiplication arithmetic operation (<c>*</c>).
        /// </summary>
        Multiply = 14,

        /// <summary>
        /// Division arithmetic operation (<c>/</c>).
        /// </summary>
        Divide = 15,

        /// <summary>
        /// Equality comparison operator (<c>=</c>).
        /// </summary>
        Equal = 16,

        /// <summary>
        /// Pattern matching operator (<c>LIKE</c>).
        /// </summary>
        Like = 17,

        /// <summary>
        /// Range comparison operator (<c>BETWEEN</c>).
        /// </summary>
        Between = 18,

        /// <summary>
        /// Greater than comparison operator (<c>&gt;</c>).
        /// </summary>
        GreaterThan = 19,

        /// <summary>
        /// Greater than or equal comparison operator (<c>&gt;=</c>).
        /// </summary>
        GreaterThanOrEqual = 20,

        /// <summary>
        /// Less than comparison operator (<c>&lt;</c>).
        /// </summary>
        LessThan = 21,

        /// <summary>
        /// Less than or equal comparison operator (<c>&lt;=</c>).
        /// </summary>
        LessThanOrEqual = 22,

        /// <summary>
        /// Inequality comparison operator (<c>!=</c>).
        /// </summary>
        NotEqual = 23,

        /// <summary>
        /// Set membership operator (<c>IN</c>).
        /// </summary>
        In = 24,

        /// <summary>
        /// Logical OR operator.
        /// </summary>
        Or = 25,

        /// <summary>
        /// Logical AND operator.
        /// </summary>
        And = 26,

        /// <summary>
        /// Map/transform operation for collections.
        /// </summary>
        Map = 27,

        /// <summary>
        /// Filter operation for collections.
        /// </summary>
        Filter = 28,

        /// <summary>
        /// Sort operation for collections.
        /// </summary>
        Sort = 29,

        /// <summary>
        /// Source collection reference (<c>*</c>).
        /// </summary>
        Source = 30,

        /// <summary>
        /// Vector similarity search operation.
        /// </summary>
        VectorSim = 50
    }
}
