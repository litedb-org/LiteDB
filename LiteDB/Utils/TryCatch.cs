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
#if DEBUG || TESTING
        // The teardown step the next Catch runs (see Step); cleared when that Catch consumes it.
        private string _step;
#endif

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

        /// <summary>
        /// Name the next <see cref="Catch"/> as teardown step <paramref name="step"/> of a registered
        /// teardown path (see <see cref="TeardownSteps"/>): a sweep's skip fault is thrown inside the
        /// try before the action, its fail-inside fault after it, so either is collected like a real
        /// failure of that action. Counts the step's fault-point marker. Production builds remove it.
        /// </summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        public void Step(string step, bool applies = true)
        {
#if DEBUG || TESTING
            if (!applies) return;
            Reachability.FaultPoint(step);
            _step = step;
#endif
        }

        [DebuggerHidden]
        public void Catch(Action action)
        {
#if DEBUG || TESTING
            var step = _step;
            _step = null;
#endif
            try
            {
#if DEBUG || TESTING
                if (step != null) TeardownSteps.Reach(step, TeardownStepSite.Before);
#endif
                action();
#if DEBUG || TESTING
                if (step != null) TeardownSteps.Reach(step, TeardownStepSite.After);
#endif
            }
            catch (Exception ex)
            {
                this.Exceptions.Add(ex);
            }
        }
    }
}
