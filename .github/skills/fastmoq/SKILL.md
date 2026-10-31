---
name: fastmoq
description:
  Write or migrate FastMoq tests with xUnit v3 and Microsoft.Testing.Platform v2.
  Covers setup, async cleanup, tracked mocks, dependency overrides, helpers,
  and provider selection.
---

# FastMoq with xUnit v3

Use .NET 10, pinned FastMoq v4 and `xunit.v3.mtp-v2` packages, and xUnit `Assert`.
Keep MTP selected in `global.json`; do not replace it or add VSTest adapters.
Follow [AGENTS.md](../../../AGENTS.md).

From the repository root:

```powershell
dotnet test --project tests/OssmSharp.Web.Api.Tests/OssmSharp.Web.Api.Tests.csproj
```

For filters, test results, and configuration, use [fastmoq-mtp2](../fastmoq-mtp2/SKILL.md).

## Defaults

- Prefer `FastMoq.Core`; ask before adding packages. Web and EF helpers need
  `FastMoq.Web` and `FastMoq.Database`. Align EF package majors.
- Check `MockerTestBase<T>` first and use it when supported. Otherwise use `Mocker`.
- Use public `[Fact]` or `[Theory]` methods with `[InlineData]` / `[MemberData]`.
  Async tests return `Task` or `ValueTask`, never `async void`.
- Each test gets a new class instance. Arrange in `SetupMocksAction` before
  `Component` exists; the derived constructor is too late. Choose constructors
  with `ComponentConstructorParameterTypes`.
- Prefer shared helpers, then tracked extensions, then `AsMoq()` /
  `AsNSubstitute()`. Do not add verification-only wrappers.

## Construct, arrange, verify

These domain types are examples. FastMoq creates `Component` and tracks its
dependencies:

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

Later setup examples use `mocks` from `SetupMocksAction`. When the base is
unsuitable, create a fresh `Mocker`, arrange, then call `CreateInstance<T>()`
inside a `[Fact]`. Do not run tests in top-level statements.

| Intent                   | Pattern                                                                                                                                 |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------- |
| Tracked collaborator     | `GetOrCreateMock<T>()`; use `.Instance` for its object                                                                                  |
| Resolve only the object  | `GetObject<T>()` or `GetRequiredObject<T>()`                                                                                            |
| Fixed instance / factory | `AddType<IClock>(fakeClock)` / `AddType<IClock>(_ => fakeClock)`                                                                        |
| Exact async result       | `AddMethodResultAsync<IReader, string>(r => r.ReadAsync("input"), "data")`                                                              |
| Separate keyed roles     | `GetOrCreateMock<T>(new MockRequestOptions { ServiceKey = "primary" })`; create a distinct secondary role                               |
| Detached mock            | `CreateStandaloneFastMock<T>()`; wire `.Instance` manually and verify that handle through `MockingProviderRegistry.Default.Verify(...)` |

Use one resolution model per service. Do not re-register a tracked mock's
`.Instance` with `AddType`. Arrange exact-result helpers before injection.
Use tracked Moq setups for sequences or richer behavior.

Use `TimesSpec.Once`, `NeverCalled`, `Exactly(n)`, `AtLeast(n)`, or `AtMost(n)`.
No count means at least once. Prefer `FastArg.Any<T>()` / `FastArg.Is<T>()` in
provider-first verification. Check each provider's matcher support.

## xUnit v3 lifetime and output

- Await async calls and `Assert.ThrowsAsync<T>()`. Pass
  `TestContext.Current.CancellationToken`; do not cache context or block with
  `.Result` / `.Wait()`.
- `Xunit.IAsyncLifetime.InitializeAsync()` returns `ValueTask` and runs after
  construction. Use a plain class with `Mocker` if async setup must precede creation.
- Use `ValueTask DisposeAsync()` via `IAsyncDisposable` or `IAsyncLifetime`.
  xUnit v3 prefers it to synchronous disposal. In `MockerTestBase<T>`, call
  `base.Dispose()` in `finally` after owned async cleanup.
- Keep mutable mocks per test. Use class or collection fixtures only for shared
  infrastructure. Assembly fixtures do not serialize tests.
- Inject `Xunit.ITestOutputHelper`, not `Xunit.Abstractions.ITestOutputHelper`.
  Assert behavior separately from diagnostic output.

## Files, HTTP, options, logs

Reuse the in-memory file system; do not create another for an `IFileSystem` slot.

```csharp
var files = mocks.GetFileSystem();
files.Directory.CreateDirectory(@"/tests");
files.File.WriteAllText(@"/tests/input.txt", "data");

mocks.SetupOptions(new ClientOptions { RetryCount = 2 });
mocks.WhenHttpRequestJson(HttpMethod.Get, "/items/42", "{\"id\":42}");
mocks.WhenHttpRequest(HttpMethod.Get, "/missing", () =>
    new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
```

After exercising `Component`:

```csharp
Mocks.VerifyLogged(
    Microsoft.Extensions.Logging.LogLevel.Warning, "retrying", TimesSpec.Exactly(2));
```

`IHttpClientFactory` shares the mocker's handler but skips named-client configuration.
Use `AddTypedServiceProvider(...)` / `CreateTypedServiceScope(...)` when service
types and lifetimes matter. Never return one object for every service.

## DbContext (optional database package)

`GetMockDbContext<T>()` creates Moq-backed sets. Seed via `GetRequiredObject<T>()`;
retrieve the tracked handle after helper creation. For a real EF in-memory context:

```csharp
var db = mocks.GetDbContextHandle<AppDbContext>(new DbContextHandleOptions<AppDbContext>
{
    Mode = DbContextTestMode.RealInMemory,
});
db.Context.Database.EnsureCreated();
```

Seed before acting, isolate stores per test, and dispose owned contexts.
In-memory tests do not prove SQL or transaction behavior.

## Controllers and request context (optional web package)

Import `FastMoq.Web.Extensions`. When direct construction is needed, arrange in
a plain `[Fact]`, not an extra controller in a base setup hook:

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

Assign context to the subject. Direct action calls bypass authorization middleware.

## Known-type customization

Use `AddType` to replace dependencies. Reserve `AddKnownType` for framework
resolution or post-processing. Registrations stay local:

```csharp
mocks.AddKnownType<Microsoft.AspNetCore.Http.HttpContext>(
    applyObjectDefaults: (_, context) => context.TraceIdentifier = "test-1",
    replace: true);
```

## Providers and migration

Provider order is an async-scoped `Push`, explicit default, sole discoverable
non-reflection provider, then `reflection`. Reflection only compares direct
constant arguments with `Equals`; do not rely on it for captured values or predicates.

Moq `.Setup(...)` needs `FastMoq.Provider.Moq` and
`using FastMoq.Providers.MoqProvider;`. Select it once before test-class construction:

```csharp
using FastMoq.Providers;
[assembly: FastMoqDefaultProvider("moq")]
```

Register and select with
`FastMoqRegisterProvider("alias", typeof(Provider), SetAsDefault = true)`.
For temporary overrides, create the mocker inside the scope. `Push` cannot
reconfigure an existing `Component`:

```csharp
using var scope = MockingProviderRegistry.Push("reflection");
var mocks = new Mocker();
```

Do not change global defaults in parallel tests. Preserve intentional
provider-specific behavior during migration.

| Legacy                             | Preferred v4                                                                 |
| ---------------------------------- | ---------------------------------------------------------------------------- |
| `GetMock<T>()`, tracked `.Object`  | `GetOrCreateMock<T>()`, `.Instance`                                          |
| `VerifyLogger(...)`                | `VerifyLogged(...)`                                                          |
| `SetupHttpMessage(...)`            | `WhenHttpRequest(...)` / `WhenHttpRequestJson(...)`                          |
| Moq `Times` in shared verification | `TimesSpec`; never confuse `NeverCalled` with a nonexistent `Never` property |
| `Strict` as a whole profile        | `UseStrictPreset()`; `FailOnUnconfigured` alone is narrower                  |
| `MockOptional`                     | Explicit `OptionalParameterResolution` / component creation flags            |

## Sources

Reviewed 2026-09-14: [Getting started](https://help.fastmoq.com/docs/getting-started/README.html),
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
