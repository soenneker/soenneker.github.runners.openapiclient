using System.Threading;
using Soenneker.Tests.HostedUnit;
using System.Threading.Tasks;
using Soenneker.OpenApi.Fixer.Abstract;

namespace Soenneker.GitHub.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public class GitHubOpenApiClientRunnerTests : HostedUnitTest
{
    private readonly IOpenApiFixer _fixer;

    public GitHubOpenApiClientRunnerTests(Host host) : base(host)
    {
        _fixer = Resolve<IOpenApiFixer>(true);
    }

    [Test]
    public void Default()
    {
    }

   // [Skip("Manual")]
    public async ValueTask Fix(CancellationToken cancellationToken)
    {
        await _fixer.Fix(@"c:\github\github.json", @"c:\github\fixed.json", cancellationToken);
    }
}
