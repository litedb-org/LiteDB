using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiteDB.Shell
{
    /// <summary>
    /// Very simple class that parse command line arguments 
    /// </summary>
    internal class OptionSet
    {
        private readonly Dictionary<string, OptionsParam> _options = new Dictionary<string, OptionsParam>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Register all extra non parameter (without -- or /)
        /// </summary>
        public void Register(Action<string> action)
        {
            _options.Add("_extra_",
                new OptionsParam { Action = (value) => action((string)value) });
        }

        /// <summary>
        /// Register a parameter with value and data type (like --path "C:\temp\times.txt")
        /// </summary>
        public void Register<T>(string key, Action<T> action)
        {
            _options.Add(key,
                new OptionsParam { Action = (value) => action((T)value), Type = typeof(T) });
        }

        /// <summary>
        /// Register a parameter without any value (like --help)
        /// </summary>
        public void Register(string key, Action action)
        {
            _options.Add(key,
                new OptionsParam { Action = (value) => action() });
        }

        /// <summary>
        /// Parse command line args calling register parameters
        /// </summary>
        public void Parse(string[] args)
        {
            var expr = new Regex(@"^(--|-|\/)(\w+)([=:]?)");

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == ">" || arg == "|") break;

                var match = expr.Match(arg);

                OptionsParam param;

                if (match.Success)
                {
                    var key = match.Groups[2].Value;
                    var equals = match.Groups[3].Value;

                    // get OptionsItem match key (if not found, ignore param)
                    if (!_options.TryGetValue(key, out param)) continue;

                    // parameterless
                    if (param.Type == null)
                    {
                        param.Action(null);
                        continue;
                    }

                    // when value is on same arg (like --param=value)
                    if (equals.Length > 0)
                    {
                        var value = arg.Substring(match.Value.Length);
                        var val = (object)Convert.ChangeType(value, param.Type);
                        param.Action(val);
                    }
                    else
                    {
                        // when value are on next arg (like --param value)
                        if (i < args.Length - 1)
                        {
                            var value = args[++i];
                            var val = (object)Convert.ChangeType(value, param.Type);
                            param.Action(val);
                        }
                        else
                        {
                            param.Action(null);
                        }
                    }
                }
                else
                {
                    // call extra
                    if (_options.TryGetValue("_extra_", out param))
                    {
                        param.Action(arg);
                    }
                }
            }
        }
    }

    internal class OptionsParam
    {
        public Type Type { get; set; }
        public Action<object> Action { get; set; }
    }
}