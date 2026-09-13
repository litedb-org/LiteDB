using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LiteDB.Utils
{
    internal class TryCatch
    {
        public readonly List<Exception> Exceptions = new List<Exception>();

        public TryCatch()
        {
        }

        public TryCatch(Exception initial)
        {
            this.Exceptions.Add(initial);
        }

        public bool InvalidDatafileState => this.Exceptions.Any(ex => 
            ex is LiteException liteEx && 
            liteEx.ErrorCode == LiteException.INVALID_DATAFILE_STATE);

        [DebuggerHidden]
        public void Catch(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                this.Exceptions.Add(ex);
            }
        }
    }
}
