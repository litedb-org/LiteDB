using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace LiteDB.Security.Tests
{
    public class KnownUnsafeNamesTests
    {
        // Compatibility matrix from the v5 security backport. Fixtures define
        // harmless stand-ins; no third-party gadget implementation is loaded.
        public static IEnumerable<object[]> Names()
        {
            yield return new object[] { "System.Workflow.ComponentModel.AppSettings" };
            yield return new object[] { "System.Core" };
            yield return new object[] { "WinRT.BaseActivationFactory" };
            yield return new object[] { "System.Data" };
            yield return new object[] { "System.Windows.Data.ObjectDataProvider" };
            yield return new object[] { "System.CodeDom.Compiler.CompilerResults" };
            yield return new object[] { "System.Collections.ArrayList" };
            yield return new object[] { "System.Diagnostics.Process" };
            yield return new object[] { "System.Diagnostics.ProcessStartInfo" };
            yield return new object[] { "System.Management.Automation" };
            yield return new object[] { "System.Windows.Markup.XamlReader" };
            yield return new object[] { "System.Web.Security.RolePrincipal" };
            yield return new object[] { "System.Security.Principal.WindowsIdentity" };
            yield return new object[] { "System.Security.Principal.WindowsPrincipal" };
            yield return new object[] { "Microsoft.VisualStudio.Text.Formatting.TextFormattingRunProperties" };
            yield return new object[] { "System.Drawing.Design.ToolboxItemContainer" };
            yield return new object[] { "Microsoft.IdentityModel.Claims.WindowsClaimsIdentity" };
            yield return new object[] { "System.Resources.ResXResourceReader" };
            yield return new object[] { "System.Resources.ResXResourceWriter" };
            yield return new object[] { "System.Windows.Forms" };
            yield return new object[] { "Microsoft.ApplicationId.Framework.InfiniteProgressPage" };
            yield return new object[] { "Microsoft.VisualBasic.Logging.FileLogTraceListener" };
            yield return new object[] { "Grpc.Core.Internal.UnmanagedLibrary" };
            yield return new object[] { "MongoDB.Libmongocrypt.LibraryLoader+WindowsLibrary" };
            yield return new object[] { "Xunit.Xunit1Executor" };
            yield return new object[] { "Apache.NMS.ActiveMQ.Commands.ActiveMQObjectMessage" };
            yield return new object[] { "Apache.NMS.ActiveMQ.Transport.Failover.FailoverTransport" };
            yield return new object[] { "Apache.NMS.ActiveMQ.Util.IdGenerator" };
            yield return new object[] { "Xunit.Sdk.TestFrameworkDiscoverer+PreserveWorkingFolder" };
            yield return new object[] { "Xunit.Xunit1AssemblyInfo" };
            yield return new object[] { "Amazon.Runtime.Internal.Util.OptimisticLockedTextFile" };
            yield return new object[] { "Microsoft.Azure.Cosmos.Query.Core.QueryPlan.QueryPartitionProvider" };
            yield return new object[] { "NLog.Internal.FileAppenders.SingleProcessFileAppender" };
            yield return new object[] { "NLog.Targets.FileTarget" };
            yield return new object[] { "Google.Apis.Util.Store.FileDataStore" };
        }

        [Theory]
        [MemberData(nameof(Names))]
        public void Every_backported_name_is_blocked_before_instantiation(string fullName)
        {
            var assemblyName = new AssemblyName("UnsafeNameFixture" + Guid.NewGuid().ToString("N"));
            var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule(assemblyName.Name);
            var names = fullName.Split('+');
            var outer = module.DefineType(names[0], TypeAttributes.Public);
            var builder = names.Length == 1 ? outer :
                outer.DefineNestedType(names[1], TypeAttributes.NestedPublic);
            builder.DefineDefaultConstructor(MethodAttributes.Public);
            var fixture = builder.CreateTypeInfo().AsType();
            if (builder != outer)
            {
                outer.CreateTypeInfo();
            }
            Assert.Equal(fullName, fixture.FullName);
            ResolveEventHandler resolve = (sender, args) =>
                new AssemblyName(args.Name).Name == assemblyName.Name ? assembly : null;
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try
            {
                var attempted = false;
                var mapper = new BsonMapper(type => { attempted = true; throw new InvalidOperationException(); });
                var failure = Assert.Throws<LiteException>(() => mapper.ToObject<object>(
                    new BsonDocument { ["_type"] = fixture.AssemblyQualifiedName }));
                Assert.Equal(fullName == "System.Diagnostics.Process" ? LiteException.AVOID_USE_OF_PROCESS : LiteException.ILLEGAL_DESERIALIZATION_TYPE,
                    failure.ErrorCode);
                Assert.Contains(fullName, failure.Message);
                Assert.False(attempted);
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolve;
            }
        }
    }
}
