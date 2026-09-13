using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace LiteDB.Stress
{
    public interface ITestItem
    {
        string Name { get; }
        int TaskCount { get; }
        TimeSpan Sleep { get; }
        BsonValue Execute(LiteDatabase db);
    }
}
