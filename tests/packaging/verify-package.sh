#!/usr/bin/env bash
# Consumes the built packages the way a real user would, from a clean project with no reference
# to this repository's source, optionally under Native AOT.
#
# This catches a package that builds, tests and installs cleanly and then fails at the consumer's
# first call, and trim or AOT warnings that only a consumer sees. Adapted from
# sanamhub/ada-csharp tests/packaging/verify-package.sh.
set -euo pipefail

PACKAGE_DIR=""
AOT="false"

while [ $# -gt 0 ]; do
  case "$1" in
    --package-dir) PACKAGE_DIR="$2"; shift 2 ;;
    --aot)         AOT="true";       shift 1 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[ -n "$PACKAGE_DIR" ] || { echo "--package-dir is required" >&2; exit 2; }
PACKAGE_DIR="$(cd "$PACKAGE_DIR" && pwd)"

# The core package's file name has the version digit right after "FreeTierMail.". The other
# packages (FreeTierMail.Smtp.x.y.z, FreeTierMail.Testing.x.y.z) do not, so this glob finds the core alone.
PACKAGES=("$PACKAGE_DIR"/FreeTierMail.[0-9]*.nupkg)
if [ ! -f "${PACKAGES[0]}" ]; then
  echo "no FreeTierMail package found in $PACKAGE_DIR" >&2
  exit 1
fi
VERSION="$(basename "${PACKAGES[0]}" .nupkg)"
VERSION="${VERSION#FreeTierMail.}"

# Under Git Bash, pwd returns an MSYS path that .NET cannot read.
NATIVE_PACKAGE_DIR="$PACKAGE_DIR"
if command -v cygpath >/dev/null 2>&1; then
  NATIVE_PACKAGE_DIR="$(cygpath -w "$PACKAGE_DIR")"
fi

echo "consuming FreeTierMail $VERSION from $NATIVE_PACKAGE_DIR (aot=$AOT)"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

# Our packages can only come from the local folder, everything else only from nuget.org. A
# local-only feed would starve Native AOT, which restores the ILCompiler packages from nuget.org.
cat > NuGet.Config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$NATIVE_PACKAGE_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="FreeTierMail" />
      <package pattern="FreeTierMail.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML

# Warnings are errors, so a trim or AOT warning (IL2xxx, IL3xxx) from our packages fails here.
cat > consumer.csproj <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Set explicitly. This project lives in a temp directory on purpose, so it inherits none
         of the repository's Directory.Build.props. -->
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Consumer</RootNamespace>
    <PublishAot>$AOT</PublishAot>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FreeTierMail" Version="$VERSION" />
  </ItemGroup>
</Project>
XML

cat > Program.cs <<'CS'
using System.Net;
using System.Text;
using FreeTierMail;
using FreeTierMail.Resend;

// Exercises source-generated JSON, quota routing and the idempotency cache, the parts most likely
// to break under trimming.
var handler = new Stub();
var mailer = new FreeTierMailer([new ResendProvider(new HttpClient(handler), new ResendOptions { ApiKey = "test-key-0000000000000000", Daily = 1 })],
    new FreeTierMailerOptions { CriticalReserve = 0 });
var message = new EmailMessage(new EmailAddress("links@example.org"), [new EmailAddress("rider@example.com")], "Sign in") { TextBody = "Link", IdempotencyKey = "k" };

var first = await mailer.SendAsync(message);
var repeat = await mailer.SendAsync(message);
var overQuota = await mailer.SendAsync(new EmailMessage(new EmailAddress("links@example.org"), [new EmailAddress("rider@example.com")], "Again") { TextBody = "Link" });

if (first.Status != SendStatus.Sent) { Console.Error.WriteLine($"FAIL: first was {first.Status}"); return 1; }
if (!repeat.IsReplay || handler.Calls != 1) { Console.Error.WriteLine("FAIL: the repeat sent again"); return 1; }
if (overQuota.Status != SendStatus.Failed) { Console.Error.WriteLine($"FAIL: over quota was {overQuota.Status}"); return 1; }
if (!handler.Body.Contains("\"to\":[\"rider@example.com\"]", StringComparison.Ordinal)) { Console.Error.WriteLine($"FAIL: body was {handler.Body}"); return 1; }

Console.WriteLine("PASS");
return 0;

sealed class Stub : HttpMessageHandler
{
    public string Body { get; private set; } = "";

    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Body = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"1\"}", Encoding.UTF8, "application/json") };
    }
}
CS

dotnet restore --verbosity quiet

if [ "$AOT" = "true" ]; then
  dotnet publish -c Release -o out --verbosity quiet
  ./out/consumer
else
  dotnet run -c Release --verbosity quiet
fi

echo "package consumption OK"
