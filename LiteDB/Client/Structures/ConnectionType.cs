﻿using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public enum ConnectionMode
    {
        Exclusive,
        Shared
    }
}