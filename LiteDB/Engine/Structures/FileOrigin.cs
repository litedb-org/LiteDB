namespace LiteDB.Engine
{
    public enum FileOrigin : byte
    {
        /// <summary>
        /// There is no origin (new page)
        /// </summary>
        None = 0,

        /// <summary>
        /// Data file 
        /// </summary>
        Data = 1,

        /// <summary>
        /// Log file (-log)
        /// </summary>
        Log = 2
    }
}
