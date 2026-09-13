using System;
using System.Collections.Generic;
using System.Reflection;

namespace LiteDB
{
    public class DefaultTypeNameBinder : ITypeNameBinder
    {
        public static DefaultTypeNameBinder Instance { get; } = new DefaultTypeNameBinder();

        /// <summary>
        /// Contains all well known vulnerable types according to ysoserial.net
        /// </summary>
        private static readonly HashSet<string> _disallowedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Workflow.ComponentModel.AppSettings",
            "System.Core",
            "WinRT.BaseActivationFactory",
            "System.Data",
            "System.Windows.Data.ObjectDataProvider",
            "System.CodeDom.Compiler.CompilerResults",
            "System.Collections.ArrayList",
            "System.Diagnostics.Process",
            "System.Diagnostics.ProcessStartInfo",
            "System.Management.Automation",
            "System.Windows.Markup.XamlReader",
            "System.Web.Security.RolePrincipal",
            "System.Security.Principal.WindowsIdentity",
            "System.Security.Principal.WindowsPrincipal",
            "Microsoft.VisualStudio.Text.Formatting.TextFormattingRunProperties",
            "System.Drawing.Design.ToolboxItemContainer",
            "Microsoft.IdentityModel.Claims.WindowsClaimsIdentity",
            "System.Resources.ResXResourceReader",
            "System.Resources.ResXResourceWriter",
            "System.Windows.Forms",
            "Microsoft.ApplicationId.Framework.InfiniteProgressPage",
            "Microsoft.VisualBasic.Logging.FileLogTraceListener",
            "Grpc.Core.Internal.UnmanagedLibrary",
            "MongoDB.Libmongocrypt.LibraryLoader+WindowsLibrary",
            "Xunit.Xunit1Executor",
            "Apache.NMS.ActiveMQ.Commands.ActiveMQObjectMessage",
            "Apache.NMS.ActiveMQ.Transport.Failover.FailoverTransport",
            "Apache.NMS.ActiveMQ.Util.IdGenerator",
            "Xunit.Sdk.TestFrameworkDiscoverer+PreserveWorkingFolder",
            "Xunit.Xunit1AssemblyInfo",
            "Amazon.Runtime.Internal.Util.OptimisticLockedTextFile",
            "Microsoft.Azure.Cosmos.Query.Core.QueryPlan.QueryPartitionProvider",
            "NLog.Internal.FileAppenders.SingleProcessFileAppender",
            "NLog.Targets.FileTarget",
            "Google.Apis.Util.Store.FileDataStore",
        };

        private DefaultTypeNameBinder()
        {
        }

        public string GetName(Type type) => type.FullName + ", " + type.GetTypeInfo().Assembly.GetName().Name;

        public Type GetType(string name)
        {
            var type = Type.GetType(name);
            if (type == null)
            {
                return null;
            }

            if (_disallowedTypeNames.Contains(type.FullName))
            {
                throw LiteException.IllegalDeserializationType(type.FullName);
            }

            return type;
        }
    }
}