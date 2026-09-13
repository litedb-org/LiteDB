using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    public enum BsonExpressionType : byte
    {
        Double = 1,
        Int = 2,
        String = 3,
        Boolean = 4,
        Null = 5,
        Array = 6,
        Document = 7,

        Parameter = 8,
        Call = 9,
        Path = 10,

        Modulo = 11,
        Add = 12,
        Subtract = 13,
        Multiply = 14,
        Divide = 15,

        Equal = 16,
        Like = 17,
        Between = 18,
        GreaterThan = 19,
        GreaterThanOrEqual = 20,
        LessThan = 21,
        LessThanOrEqual = 22,
        NotEqual = 23,
        In = 24,

        Or = 25,
        And = 26,

        Map = 27,
        Filter = 28,
        Sort = 29,
        Source = 30,
        VectorSim = 50
    }
}
