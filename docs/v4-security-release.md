# v4 deserialization security release candidate

This branch prepares LiteDB 4.1.5 for issue #2808. It is not a published release.
The existing v4 assignability check and Process rejection are retained. The
backport adds the 35 type names rejected by the v5 default binder and exposes
`BsonMapper.TypeNameBinder` for application-specific type resolution. Process
continues to raise error 215; other blocked types raise error 218. Unknown names
raise 207 and otherwise allowed incompatible types raise 214 before the type instantiator runs.
Denied names are rejected before assignability is checked, matching v5; an
incompatible denied name therefore raises 215/218 rather than 214.
The default serialization discriminator remains `Full.Type.Name, AssemblyName`.

## Applications reading untrusted documents

The default deny-list is a compatibility backport, not a general guarantee that
arbitrary .NET objects are safe to deserialize. Entries are exact resolved type
names, not assembly or namespace bans; even the inherited entries named
`System.Core`, `System.Data`, `System.Windows.Forms`, and
`System.Management.Automation` have only that exact-name meaning. The default
binder does not inspect a permitted type's generic arguments, base classes, or
member graph. A permitted wrapper can construct a denied member type from its
declared metadata when that member has no discriminator of its own. This
backport deliberately matches the v5 policy rather than widening it to whole
assemblies or changing explicitly requested model behavior. Prefer BSON documents for
untrusted data. Where polymorphic object mapping is required, configure a binder
that maps only explicit, trusted application types, without falling back to
`Type.GetType`. For example:

```csharp
public sealed class ApplicationTypeBinder : ITypeNameBinder
{
    public string GetName(Type type)
    {
        if (type == typeof(ApprovedRecord)) return "approved-record";
        throw new InvalidOperationException("Unregistered application type");
    }

    public Type GetType(string name)
    {
        return name == "approved-record" ? typeof(ApprovedRecord) : null;
    }
}

var mapper = new BsonMapper { TypeNameBinder = new ApplicationTypeBinder() };
```

`ApprovedRecord` represents a trusted application class. Custom binders replace
the default binder and are trusted application code. Configure the mapper before
use. Register any existing discriminator strings that the application still
needs to read; changing the binder does not rewrite stored documents. Constructor,
property-setter, custom deserializer, and explicitly requested model behavior
remain the application's responsibility.

## Reproduce validation and prepare the package

An isolated .NET 8 xUnit project avoids changing the legacy MSTest test suite.
Tests use harmless type fixtures and stop before constructing blocked types.
They cover all 35 names, root/object-member/dictionary/array mapping,
assignability, unknown names, default discriminator compatibility, and a custom
allow-list binder.

From the repository root, with a current .NET SDK and the .NET 8 runtime:

```sh
dotnet test LiteDB.Security.Tests -c Release
dotnet pack LiteDB/LiteDB.csproj -c Release '-p:TargetFrameworks="net35;net40;netstandard1.3;netstandard2.0"' -p:SignAssembly=true -o artifacts_temp/v4-security
```

The explicit framework list and signing flag are required on non-Windows hosts
to retain all four original package assets and the existing strong-name key.
Use an empty output directory and inspect the resulting 4.1.5 package before
publication. The test project also accepts `-p:LiteDBAssembly=/absolute/path/LiteDB.dll`
to test an extracted package assembly without a project reference. The workflow also runs a small framework-native harness against the same
packaged net35/net40 assets on Windows (CLR 2.0 and CLR 4.0 respectively). It
checks the runtime, assembly version and signing-key token, representative
blocked types across four shapes, safe round trips, and binder/assignability
behavior. Publication requires both Windows jobs to pass. Enabling .NET 3.5
must succeed without a pending restart; a missing runtime fails the gate.
Locally, those harnesses can be built with `dotnet build
LiteDB.Security.FrameworkTests -c Release -f net35 -p:LiteDBAssembly=...`
(or `net40`) and their executables run on Windows with argument `2` (or `4`).

## Publication remains a separate step

After review and release approval, integrate this commit into `v4`, validate
that exact commit, create the annotated `v4.1.5` tag, and publish its signed
`LiteDB.4.1.5.nupkg` to NuGet. Check that NuGet serves all four framework assets.
The v4 branch includes a `publish-release.yml` workflow that builds and
tests this legacy package on v4 pull requests and branch pushes. Dispatch that workflow from this branch with
`publish_nuget=false` to validate and upload a candidate. Publication defaults
to off and requires an annotated `v4.1.5` tag at the checked-out commit already
in `origin/v4`; its credential comes from the existing repository Bitwarden
integration. The modern default-branch workflow cannot build this branch, so
select the v4 workflow revision explicitly when dispatching. PR #2465 is already closed and needs no superseding closure.

The published GHSA-3x49-g6rc-c284 advisory currently marks versions below 5.0.13
as affected. Its ranges must be updated only after 4.1.5 is available, so that
4.1.5 is recognized as patched while the pre-5.0.13 v5 releases remain affected.
An appropriate split is `< 4.1.5` (patched 4.1.5) and `>= 5.0.0, < 5.0.13`
(patched 5.0.13), subject to maintainer verification of published versions.
Do not claim issue #2808 is resolved until publication and advisory verification.

Users choosing to migrate can upgrade v4 files with v5's `Upgrade=true`
connection-string setting. There is no v5-to-v4 downgrade API. See issue #2405
for upgrade failures involving duplicate keys.
