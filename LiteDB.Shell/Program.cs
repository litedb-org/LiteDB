namespace LiteDB.Shell
{
    internal class Program
    {
        /// <summary>
        /// Opens console shell app. Usage:
        /// LiteDB.Shell [myfile.db] --param1 value1 --params2 "value 2"
        /// Parameters:
        /// --exec "command"   : Execute an shell command (can be multiples --exec)
        /// --run script.txt   : Run script commands file
        /// --pretty           : Show JSON in multiline + idented
        /// --exit             : Exit after last command
        /// </summary>
        private static void Main(string[] args)
        {
            var input = new InputCommand();
            var display = new Display();
            var o = new OptionSet();

            // default arg
            o.Register((v) => input.Queue.Enqueue("open " + v));
            o.Register("pretty", () => display.Pretty = true);
            o.Register("exit", () => input.AutoExit = true);
            o.Register<string>("run", (v) => input.Queue.Enqueue("run " + v));
            o.Register<string>("exec", (v) => input.Queue.Enqueue(v));

            // parse command line calling register parameters
            o.Parse(args);

            ShellProgram.Start(input, display);
        }
    }
}