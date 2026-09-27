param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [Parameter(Mandatory)][string] $PackageVersion,
    [Parameter(Mandatory)][version] $AssemblyVersion
)

$ErrorActionPreference = 'Stop'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('litedb-package-versions-' + [guid]::NewGuid())
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null

try {
    foreach ($packageId in @('LiteDB', 'LiteDB.SourceGenerator')) {
        $packagePath = Join-Path $PackageDirectory "$packageId.$PackageVersion.nupkg"
        $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $packagePath))
        try {
            $assemblies = @($archive.Entries | Where-Object { $_.FullName.EndsWith("/$packageId.dll") })
            if ($assemblies.Count -eq 0) {
                throw "No $packageId assembly in $packagePath"
            }

            foreach ($entry in $assemblies) {
                $destination = Join-Path $temporaryDirectory ($entry.FullName.Replace('/', '-'))
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination)
                $actual = [Reflection.AssemblyName]::GetAssemblyName($destination).Version
                if ($actual -ne $AssemblyVersion) {
                    throw "$packagePath/$($entry.FullName) has AssemblyVersion $actual; expected $AssemblyVersion. Rebuild with release versioning before packing."
                }
                Write-Output "[PACKAGE-VERSION] $packageId/$($entry.FullName): $actual"
            }
        }
        finally {
            $archive.Dispose()
        }
    }
}
finally {
    [IO.Directory]::Delete($temporaryDirectory, $true)
}
