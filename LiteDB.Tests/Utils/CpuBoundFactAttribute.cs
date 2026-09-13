using System;
using Xunit;

namespace LiteDB.Tests.Utils;

class CpuBoundFactAttribute : FactAttribute
{
    public CpuBoundFactAttribute(int minCpuCount = 1)
    {
        if (minCpuCount > Environment.ProcessorCount)
        {
            Skip = $"This test requires at least {minCpuCount} processors to run properly.";
        }   
    }
}