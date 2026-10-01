---
name: fastmoq
description:
  Write or migrate FastMoq tests for xUnit v3 with Microsoft.Testing.Platform
  v2. Use for Fact/Theory tests, per-test setup, async cleanup, tracked mocks,
  dependency overrides, framework helpers, and provider selection.
---

# FastMoq with xUnit v3

Use .NET 10, the pinned FastMoq v4 and `xunit.v3.mtp-v2` packages, and xUnit
`Assert` methods. Keep `Microsoft.Testing.Platform` selected in `global.json`;
do not add VSTest adapters or replace the runner. Follow
[AGENTS.md](../../../AGENTS.md).

From the repository root:

```powershell
dotnet test --project tests/OssmSharp.Web.Api.Tests/OssmSharp.Web.Api.Tests.csproj
```

For filters, reports, and project configuration, use
[fastmoq-mtp2](../fastmoq-mtp2/SKILL.md).

## Defaults

- Prefer `FastMoq.Core`; ask before adding packages. Web helpers need
  `FastMoq.Web`, EF helpers need `FastMoq.Database`. Keep EF package majors
  aligned.
- Keep pure tests and deterministic fakes direct. Use `Mocker` for local DI
  construction; `MockerTestBase<T>` for a repeated component harness.
- Use public `[Fact]` methods, or `[Theory]` with `[InlineData]` /
  `[MemberData]`. Async tests return `Task` or `ValueTask`, never `async void`.
- xUnit creates a test-class instance per test. In a FastMoq base, arrange in
  `SetupMocksAction` before `Component` is created; the derived constructor body
  is too late for dependency replacement. Select constructor signatures with
  `ComponentConstructorParameterTypes`.
- Prefer shared helpers, then tracked provider extensions, then explicit
  `AsMoq()` / `AsNSubstitute()` escape hatches. Do not add verification-only
  wrappers.

## Construct, arrange, verify

Domain types are illustrative. FastMoq creates `Component` and its tracked
dependencies; the test runner discovers the attributed method:

```csharp
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Xunit;

public sealed class ImporterTests : MockerTestBase<Importer>
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.AddMethodResultAsync<IReader, string>(
            r => r.ReadAsync("input", FastArg.Any<CancellationToken>()), "data");
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Imports_and_publishes()
    {
        var result = await Component.ImportAsync(
            "input", TestContext.Current.CancellationToken);

        Assert.Equal("data", result);
        Mocks.Verify<IEvents>(e => e.Publish("imported"), TimesSpec.Once);
    }
}
```

Remaining setup fragments use the `mocks` parameter in `SetupMocksAction`. For
direct construction in a plain `[Fact]`, use a fresh `new Mocker()`, arrange,
then call `CreateInstance<T>()`. Do not put test execution in top-level
statements.

| Intent                   | Pattern                                                                                                                                 |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------- |
| Tracked collaborator     | `GetOrCreateMock<T>()`; use `.Instance` for its object                                                                                  |
| Resolve only the object  | `GetObject<T>()` or `GetRequiredObject<T>()`                                                                                            |
| Fixed instance / factory | `AddType<IClock>(fakeClock)` / `AddType<IClock>(_ => fakeClock)`                                                                        |
| Exact async result       | `AddMethodResultAsync<IReader, string>(r => r.ReadAsync("input"), "data")`                                                              |
| Separate keyed roles     | `GetOrCreateMock<T>(new MockRequestOptions { ServiceKey = "primary" })`; create a distinct secondary role                               |
| Detached mock            | `CreateStandaloneFastMock<T>()`; wire `.Instance` manually and verify that handle through `MockingProviderRegistry.Default.Verify(...)` |

Keep one resolution model per service. Do not retrieve a tracked mock and then
re-register its `.Instance` with `AddType`. Exact-result helpers wrap interface
registrations; arrange them before injection, not after the subject captures a
dependency. For sequencing or richer setup, use a capable provider.

Use `TimesSpec.Once`, `NeverCalled`, `Exactly(n)`, `AtLeast(n)`, or `AtMost(n)`.
Omitting a verification count means at least once. Prefer `FastArg.Any<T>()` /
`FastArg.Is<T>(predicate)` over Moq matchers in provider-first verification;
check provider capabilities rather than assuming identical matcher behavior.

## xUnit v3 lifetime and output

- Await async calls and `Assert.ThrowsAsync<T>(...)`. Pass
  `TestContext.Current.CancellationToken` to cancellable operations; do not
  cache the test context or block with `.Result` / `.Wait()`.
- Async setup uses `Xunit.IAsyncLifetime.InitializeAsync()` returning
  `ValueTask`. It runs after construction: use a plain test class with direct
  `Mocker` when asynchronous setup must precede subject creation.
- Async cleanup uses `ValueTask DisposeAsync()` via `IAsyncDisposable` or
  `IAsyncLifetime`. v3 prefers it over `IDisposable.Dispose()`. When adding it
  to `MockerTestBase<T>`, call `base.Dispose()` in `finally` after owned async
  cleanup.
- Keep mutable mocks per test. Use `IClassFixture<T>` / collection fixtures only
  for intentionally shared infrastructure; assembly fixtures do not serialize
  tests.
- Inject `Xunit.ITestOutputHelper` for per-test diagnostics, not
  `Xunit.Abstractions.ITestOutputHelper`. Output is diagnostic; assert behavior
  separately.

## Files, HTTP, options, logs

Use the shared in-memory file system; do not create a second `MockFileSystem`
merely to fill an `IFileSystem` constructor slot.

```csharp
var files = mocks.GetFileSystem();
files.Directory.CreateDirectory(@"/tests");
files.File.WriteAllText(@"/tests/input.txt", "data");

mocks.SetupOptions(new ClientOptions { RetryCount = 2 });
mocks.WhenHttpRequestJson(HttpMethod.Get, "/items/42", "{\"id\":42}");
mocks.WhenHttpRequest(HttpMethod.Get, "/missing", () =>
    new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
```

Inside the `[Fact]`, after exercising `Component`:

```csharp
Mocks.VerifyLogged(
    Microsoft.Extensions.Logging.LogLevel.Warning, "retrying", TimesSpec.Exactly(2));
```

The built-in `IHttpClientFactory` shares the mocker's handler but does not apply
named-client configuration. Use typed DI helpers such as
`AddTypedServiceProvider(...)` / `CreateTypedServiceScope(...)` when actual
service types and lifetimes matter; never return one object for every service.

## DbContext (optional database package)

`GetMockDbContext<T>()` provides a Moq-backed convenience path for mocked sets.
Seed through `GetRequiredObject<T>()`; retrieve its tracked handle only after
helper creation. For a real EF in-memory context:

```csharp
var db = mocks.GetDbContextHandle<AppDbContext>(new DbContextHandleOptions<AppDbContext>
{
    Mode = DbContextTestMode.RealInMemory,
});
db.Context.Database.EnsureCreated();
```

Seed before the act step; isolate stores per test and dispose owned contexts
afterward. In-memory tests do not prove relational SQL or transaction behavior.

## Controllers and request context (optional web package)

Import `FastMoq.Web.Extensions`. Use this standalone arrangement in a plain
`[Fact]`, rather than creating an extra controller in a base-class setup hook:

```csharp
var mocks = new Mocker();
var http = mocks.CreateHttpContext("Admin")
    .SetRequestHeader("X-Correlation-Id", "test-1")
    .SetQueryParameter("includeInactive", "true");
mocks.AddHttpContext(http);
mocks.AddHttpContextAccessor(http);
var controller = Assert.IsType<ItemsController>(mocks.CreateInstance<ItemsController>());
controller.ControllerContext = mocks.CreateControllerContext(http);
```

For exact claims in a `MockerTestBase<ItemsController>` test body:

```csharp
var principal = Mocks.SetupClaimsPrincipal(claims,
    new FastMoq.Web.TestClaimsPrincipalOptions { IncludeDefaultIdentityClaims = false });
Component.ControllerContext = Mocks.CreateControllerContext(principal);
```

Assign the context to the actual subject, not a throwaway controller created
during setup. Direct action calls do not exercise authorization middleware.

## Known-type customization

Use `AddType` for ordinary dependency replacement. Use `AddKnownType` only for
framework resolution or post-processing; registrations stay local to the mocker:

```csharp
mocks.AddKnownType<Microsoft.AspNetCore.Http.HttpContext>(
    applyObjectDefaults: (_, context) => context.TraceIdentifier = "test-1",
    replace: true);
```

## Providers and migration

Provider resolution uses an async-scoped `Push` override, an explicit default, a
single discoverable non-reflection provider, or `reflection`. Reflection
verification is best-effort and compares only direct constant arguments with
`Equals`; do not use it to prove captured-variable or rich matcher predicates.

Moq-fluent `.Setup(...)` needs `FastMoq.Provider.Moq` and
`using FastMoq.Providers.MoqProvider;`. Select it once in the xUnit test
assembly, before any FastMoq test-class construction:

```csharp
using FastMoq.Providers;
[assembly: FastMoqDefaultProvider("moq")]
```

For register-and-select bootstrap, use
`FastMoqRegisterProvider("alias", typeof(Provider), SetAsDefault = true)`. For a
temporary override, use a plain `[Fact]` and create its mocker inside the scope;
pushing a provider cannot reconfigure an already-created `Component`:

```csharp
using var scope = MockingProviderRegistry.Push("reflection");
var mocks = new Mocker();
```

Do not change global defaults inside parallel tests. Preserve intentional
provider-specific behavior during migration; do not mechanically erase it.

| Legacy                             | Preferred v4                                                                 |
| ---------------------------------- | ---------------------------------------------------------------------------- |
| `GetMock<T>()`, tracked `.Object`  | `GetOrCreateMock<T>()`, `.Instance`                                          |
| `VerifyLogger(...)`                | `VerifyLogged(...)`                                                          |
| `SetupHttpMessage(...)`            | `WhenHttpRequest(...)` / `WhenHttpRequestJson(...)`                          |
| Moq `Times` in shared verification | `TimesSpec`; never confuse `NeverCalled` with a nonexistent `Never` property |
| `Strict` as a whole profile        | `UseStrictPreset()`; `FailOnUnconfigured` alone is narrower                  |
| `MockOptional`                     | Explicit `OptionalParameterResolution` / component creation flags            |

## Sources

Reviewed 2026-09-14:
[Getting started](https://help.fastmoq.com/docs/getting-started/README.html),
[Testing](https://help.fastmoq.com/docs/getting-started/testing-guide.html),
[Migration](https://help.fastmoq.com/docs/migration/README.html),
[Cookbook](https://help.fastmoq.com/docs/cookbook/README.html),
[Web helpers](https://help.fastmoq.com/docs/getting-started/testing-guide.html#controller-testing),
[Providers](https://help.fastmoq.com/docs/getting-started/provider-selection.html),
[API](https://help.fastmoq.com/docs/api/index.html),
[xUnit shared context](https://xunit.net/docs/shared-context),
[xUnit v3 features](https://xunit.net/docs/getting-started/v3/whats-new),
[xUnit async lifecycle](https://api.xunit.net/v3/3.0.1/Xunit.IAsyncLifetime.html),
[xUnit cleanup precedence](https://github.com/xunit/xunit/blob/v3-3.2.2/src/xunit.v3.core/Runners/TestRunner.cs).
