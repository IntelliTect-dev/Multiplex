# Multiplex

One web app that hosts many services, one per website or subject. Each service is an MCP server at its own address. For what it's for, and the list of services, see the [product overview](spec/product.md).

## Requirements

- .NET 10 SDK
- Chrome or Chromium (optional: without one configured, the first call that needs a browser downloads a Chromium)

## Run

```bash
dotnet run --project src/Multiplex.Web
```

The app serves `http://localhost:5190` (and also `https://localhost:7093` with the `https` launch profile). Point an MCP client at a service's address, for example `http://localhost:5190/time/mcp`. The health check is at `/healthz`.

## Shared browser settings

Services that read a website's pages share one Chromium. Set these in `appsettings.json`, or as environment variables (for example, `Browser__Headless=false`).

| Setting | Default | Effect |
|---|---|---|
| `Browser:ExecutablePath` | unset | Path to Chrome or Chromium. When unset, the first call downloads a Chromium, so it's slow and needs internet access. |
| `Browser:Headless` | `true` | `false` shows the browser window. |
| `Browser:StepDelayMs` | `0` | Pause between steps, so a visible run can be followed by eye. |

## Test

```bash
dotnet test
```

The tests come in three tiers:

- **Always run:** the host, routing, and anything that needs neither a browser nor the internet.
- **Need Chromium:** browser tests against saved copies of web pages, offline. Set `MULTIPLEX_CHROMIUM` to the path of a Chrome or Chromium executable (the default is `/usr/bin/chromium`). Without one, these tests are skipped.
- **Live, opt-in:** set `MULTIPLEX_LIVE_TESTS=1` (and have Chromium, as above) to call the real websites end to end. These are the tests that notice when a site changes its API or its pages.

Each service's README describes its own tests.

## Add a service

1. Find the website's API first, with the [web-api-discovery](web-api-discovery/README.md) skill. A service calls that API; the [product overview](spec/product.md) says where a browser fits.
2. Give the service its own folder, `src/Multiplex.Web/Services/<Name>/`, with its tests in `tests/Multiplex.Tests/Services/<Name>/`.
3. Mark its tool class with `[McpServerToolType]` and `[McpEndpoint("<name>")]`. The host then serves that class's tools, and only those, at `/<name>/mcp`.
4. Write a README in its folder covering its tools, their inputs and outputs, how it reaches the site (its API, or the browser and why), its quirks, its risks and its tests.
5. Add one row for it to the service catalog in the [product overview](spec/product.md).
