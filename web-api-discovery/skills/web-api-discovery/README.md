# web-api-discovery tooling

Reusable assets consumed by the [`web-api-discovery`](SKILL.md) skill (`skills/web-api-discovery/SKILL.md`)
when it scaffolds a new .NET API-wrapper project from a probed website.

These files are **input** to the skill, not generated **by** the skill. The
skill reads them, performs token substitution, and writes the result into the
user's chosen output directory.

## Layout

```
skills/web-api-discovery/
├── README.md            # this file
├── scripts/             # Node + PowerShell tooling, grouped by concern
│   ├── capture/         # record, scrub and catalogue a live session
│   │   ├── capture-har.js           # recorder + post-process: start/stop, CDP
│   │   │                            #   attach, incremental fallback recorder,
│   │   │                            #   scrub, verify, digest
│   │   ├── capture-har.test.js      # zero-dep behavior suite for the above
│   │   ├── capture-store.js         # the ONE walk over a capture store; classifies
│   │   │                            #   current / legacy / imported / not-recorder-output
│   │   ├── capture-store.test.js    # zero-dep behavior suite for the walk
│   │   ├── pacing.js                # human-scale pacing; reads throttling / blocks
│   │   │                            #   in responses and decides pause or hand-over
│   │   ├── pacing.test.js           # zero-dep behavior suite for the above
│   │   ├── sign-in.js               # the recorder signs in (Bitwarden / prompt), paced
│   │   ├── Invoke-HarCatalogue.ps1  # digest + catalogue; a folder means all of them
│   │   ├── Invoke-HarCapture.ps1    # front door: URL in, catalogue objects out
│   │   ├── Get-HarCapture.ps1       # list the recordings in the store, as objects
│   │   ├── Remove-HarCapture.ps1    # delete ONE recording, on purpose; asks first
│   │   ├── Stop-HarRecording.ps1    # end a recording (automation / AI path)
│   │   ├── ConvertFrom-HarCatalogue.ps1  # catalogue.json -> typed objects
│   │   ├── HarCapture.Format.ps1xml # console rendering for those objects
│   │   ├── catalogue-prompt.md      # the AI prompt for the catalogue phase
│   │   ├── capture-cdp.js           # predecessor; superseded by capture-har.js
│   │   └── import-mobile-app.js     # guided mobile-app capture; validates the artifact
│   ├── har/             # scrub, extract, and catalogue a captured HAR
│   │   ├── sanitize-har.js          # token/PII redaction with deterministic faker
│   │   ├── verify-scrub.js          # double-check pass; fails CI on leak
│   │   ├── Invoke-SanitizeHar.ps1   # wrapper for sanitize-har.js; a folder means
│   │   │                            #   every capture under it (resume + -Force)
│   │   ├── HarStoreBatch.psm1       # the batch loop both entry points share
│   │   ├── extract-har-reference.js # emit the reviewable API reference
│   │   ├── verify-har-reference.js  # re-check the emitted reference for leaks
│   │   ├── detect-auth.js           # classify the observed auth model
│   │   ├── har-policy.js            # loads + merges the scrub policy
│   │   ├── har-policy.default.json  # the stringent baseline (synced; do not edit)
│   │   └── ...                      # har-profile / har-literals / har-secrets /
│   │                                #   har-shapes / pii / pii-enrich
│   └── codegen/         # optional continuation: emit a buildable wrapper
│       ├── run-agent.js             # orchestrator: scrub -> verify -> detect -> generate
│       ├── generate-wrapper.js      # typed client + PowerShell module + MCP server
│       ├── tests-emit.js            # xUnit + Pester scaffold
│       ├── sln-emit.js              # deterministic .slnx
│       ├── secret-gate-emit.js      # gitleaks hook + workflows
│       └── sdlc-integration.js      # optional IntelliSDLC.ai seed
├── csharp/              # C# code templates -- token-substituted at scaffold time
│   ├── Client.cs.tmpl
│   ├── PacingHandler.cs.tmpl    # pacing + throttling DelegatingHandler (tests/PacingHandlerTests.cs.tmpl)
│   ├── Authenticator.cs.tmpl
│   ├── OAuthAuthenticator.cs.tmpl
│   ├── ISessionStore.cs.tmpl
│   ├── DpapiSessionStore.cs.tmpl
│   ├── UserSecretsSessionStore.cs.tmpl
│   └── McpProgram.cs.tmpl
├── powershell/          # PowerShell module skeleton
│   ├── Module.psm1.tmpl
│   ├── Module.psd1.tmpl
│   └── Connect.ps1.tmpl
└── config/              # Per-project config + CI templates
    ├── .gitignore.tmpl
    ├── .gitleaks.toml.tmpl
    ├── pre-commit.tmpl
    ├── Directory.Build.props.tmpl
    └── ci.yml.tmpl
```

## Token format

Templates use double-brace tokens that the skill replaces verbatim at
scaffold time:

| Token | Replaced with |
|---|---|
| `{{ProjectName}}` | PascalCase project / namespace root (e.g. `TripItEx`). |
| `{{Namespace}}` | Full .NET namespace -- defaults to `{{ProjectName}}`. |
| `{{BaseUrl}}` | Origin of the target site (e.g. `https://www.tripit.com`). |
| `{{IdpName}}` | IdP friendly name when auth model is SSO (`Google`, `Microsoft`, `Facebook`). |
| `{{AuthModel}}` | Classification token from Phase 4. |
| `{{ProjectSalt}}` | One-shot random salt for the faker substitution table. |
| `{{NowIso}}` | UTC timestamp at scaffold time, `yyyy-MM-ddTHH:mm:ssZ`. |

Templates **must not** include any real secret, token, cookie, email, or
PII -- that would short-circuit the gitleaks gate in CI on every consumer.

## Status

This directory is currently scaffolded but most assets are pending; the
skill definition references them and they will be added in follow-up PRs
tracked by issue #34. See the `.tmpl` extension as the marker for "still
to be authored".
