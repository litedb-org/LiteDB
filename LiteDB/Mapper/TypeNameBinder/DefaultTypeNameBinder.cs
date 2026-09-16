using System;
using System.Collections.Generic;
using System.Reflection;

namespace LiteDB
{
    /// <summary>Resolves type names and rejects known unsafe deserialization types.</summary>
    public class DefaultTypeNameBinder : ITypeNameBinder
    {
        /// <summary>The default stateless type-name binder.</summary>
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

        /// <summary>Returns the assembly-qualified discriminator used by the mapper.</summary>
        public string GetName(Type type) => type.FullName + ", " + type.GetTypeInfo().Assembly.GetName().Name;

        /// <summary>Resolves a discriminator, rejecting known unsafe types before construction.</summary>
        public Type GetType(string name)
        {
            var type = Type.GetType(name);
            if (type == null)
            {
                return null;
            }

            if (_disallowedTypeNames.Contains(type.FullName))
            {
                // Preserve the error code introduced by the original v4 backport.
                if (string.Equals(type.FullName, "System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase))
                    throw LiteException.AvoidUseOfProcess();
                throw LiteException.IllegalDeserializationType(type.FullName);
            }

            return type;
        }
    }
}