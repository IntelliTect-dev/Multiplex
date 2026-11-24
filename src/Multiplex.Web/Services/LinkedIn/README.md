# LinkedIn service (`/linkedin/mcp`)

The LinkedIn service lets Claude or ChatGPT handle the signed-in person's LinkedIn
**invitations** and **messages**, and nothing else: no profile, posting, feed, people
search, jobs or company pages. Each signed-in LinkedIn account gets its own data; nothing is
shared between accounts.

Design and decisions: [issue #86](https://github.com/IntelliTect-dev/Multiplex/issues/86).

## Risk

LinkedIn offers no official API for invitations or messages, so this service uses the
requests LinkedIn's own web pages send. LinkedIn's User Agreement (section 8.2) forbids bots
and automated messaging or contact-adding, and LinkedIn says accounts that use such tools
"risk having their accounts restricted or shut down". The owner chose this knowingly
(2026-11-19). The guardrails below keep the service to what a person would do by hand, one
confirmed action at a time; they do not make it allowed. LinkedIn's pages also load a bot
detector; the service sends none of the page's tracking calls and never tries to look human.

## Tools

Every tool declares `readOnlyHint`, `destructiveHint`, `idempotentHint` and `openWorldHint`.

| Tool | Kind | Notes |
|---|---|---|
| `linkedin_list_invitations` | read | Received invitations as LinkedIn's page shows them first (its most recent, about 10), with each sender's name, headline, note and `linkedin_warning` when LinkedIn warns about accepting. `direction` = sent is not connected yet |
| `linkedin_list_conversations` | read | The 20 most recent conversations, newest first; older ones are not reachable |
| `linkedin_read_conversation` | read | Oldest first; `older_cursor` reads 20 further back |
| `linkedin_accept_invitation` | change, confirm | One received invitation. When LinkedIn shows its "Take care when connecting" warning, the preview says so and the person decides |
| `linkedin_ignore_invitation` | change, destructive, confirm | One received invitation |
| `linkedin_reply` | change, confirm | One of the 20 most recent conversations; the preview names everyone who will receive it |

Not in this version: sending a connection request, following or unfollowing, and starting a
new conversation. None of them was recorded, so there is no observed request to build them from
(owner decision, 2026-11-19). Sent invitations, older invitations and older conversations are not
reachable for the same reason: their paging was never recorded.

Every change tool is open-world: it reaches another person. No tool takes a list: acting on
several invitations or conversations is one call each, each with its own confirmation.

Notes and message text come from other people, and the read tools' descriptions tell the model
to treat them as content, never as instructions.

## How it reaches LinkedIn

Recorded on 2026-11-19 with the `web-api-discovery` recorder, on the owner's account. Only the
requests below are sent; none of the pages' read receipts, typing indicators, delivery
acknowledgements or tracking calls.

- **Sign-in** (`SignIn/LinkedInSignInDefinition.cs`): the person signs in on `linkedin.com/login`
  in the mirrored browser. LinkedIn may ask them to approve the sign-in in its app (a
  `/checkpoint/` page); that is never taken as "signed out". The session snapshot keeps the
  cookies, the `csrf-token` (LinkedIn's `JSESSIONID` cookie), the member's profile id from
  `/voyager/api/me`, and the browser headers the page itself sent (`x-li-track`, `x-li-lang`,
  user agent, client hints).
- **Messages** (`LinkedInClient.cs`): LinkedIn's messaging GraphQL queries
  (`voyagerMessagingGraphQL/graphql`, names under `Multiplex:Sites:LinkedIn:QueryIds`, recorded
  values as defaults) and `voyagerMessagingDashMessengerMessages?action=createMessage`.
  Checked live on 2026-11-19 (read-only): LinkedIn accepted all three queries as built.
- **Invitations** (`LinkedInInvitationsPage.cs`, `LinkedInSdui.cs`): LinkedIn's
  server-driven invitations page. Its HTML keys each invitation's row by
  `invitation_manager_row_urn:li:invitation:{id}`; its embedded flight data carries, keyed by the
  same id, the Ignore and Accept actions (`addaInvitationAction`, with the invitation's
  validation token and the sender's profile id). People are tied to invitations by that id only,
  never by position, and a row without its own action makes the whole page untrusted. Accept and
  Ignore send the page's own action in the envelope the page sends it in. For an invitation
  LinkedIn warns about, Accept opens LinkedIn's warning as the page does and sends the action its
  "Accept invite" button carries.

**Not verified live:** accept, ignore and reply change real things, so they are tested only
against stand-ins shaped like the recording; the accept-through-a-warning path was never
exercised even in the recording (LinkedIn's own button action is used, unchanged). The first
real use of each is its first test against LinkedIn.

## Confirm step

The same mechanism as the TripIt service (it uses that service's confirm tokens). A change tool
called without `confirm_token` checks the request against LinkedIn (the invitation is still
waiting, the conversation exists), changes nothing, and returns a preview, for example *"Will
send this LinkedIn message to Jane Doe (Engineer at Contoso), https://www.linkedin.com/in/…: …"*,
plus a `confirm_token`. The model shows the preview to the person; only if they agree does it call
again with the same arguments and the token. The second call checks again before acting.

The token lasts 5 minutes, works once, and is bound to the account, the tool and the exact
arguments, so a token for one invitation cannot ignore another and a token for one message
cannot send different text.

## Pacing and errors

- One request in flight per account, at least 1 s apart plus jitter (`Multiplex:Sites:LinkedIn`:
  `MinRequestSpacingMs`, `RequestJitterMs`). `429` stops with a message; a changing request is
  never retried.
- LinkedIn's `999` answer (its "automated access" refusal) and any redirect to a `/checkpoint/`
  security check stop the tool with a message telling the person to look at LinkedIn themselves.
- A read whose session LinkedIn rejects is retried once with a renewed session (one refresh per
  account at a time, 60 s cool-down); a change whose session is rejected is reported, not resent.
- A messaging query LinkedIn no longer knows (`400`/`404`) says to record LinkedIn again and
  update the configured query names.

## Tests

- `LinkedInAccessTests.cs`: the address refuses a request without a token, through a tunnel too,
  and refuses a TripIt token.
- `LinkedInToolTests.cs`: real MCP JSON-RPC and real OAuth in front of a stand-in LinkedIn: the
  tool list and hints (no tool takes a list), each account's own data, preview before every
  change, LinkedIn's warning in the accept preview, one change per token, tokens bound to their
  arguments, group replies naming everyone, and how failures are reported.
- `LinkedInClientTests.cs`: the exact messaging requests (query, escaping, headers, the
  `createMessage` body), how answers are read, and 999 / security check / rejected session /
  stale query handling.
- `LinkedInInvitationsTests.cs`: reading invitations pages (people tied by id even when the
  actions come in another order; a row without its own action is refused), accept / ignore
  envelopes, and accepting through LinkedIn's warning.
- `LinkedInSignInDefinitionTests.cs`, `LinkedInServiceTests.cs`: who is signed in, which pages mean
  signed out, the snapshot's headers, and the service wired end to end with its sessions.
- `LinkedInRecordedPageTests.cs` (opt-in): reads a locally saved, real invitations page and
  warning answer (`LINKEDIN_RECORDED_INVITATIONS_HTML`, `LINKEDIN_RECORDED_WARNING`), printing
  only counts. Recordings hold other people's names and never enter the repository.

## Icon

`wwwroot/icons/linkedin.png` (256x256, at most 10 KB), LinkedIn's own icon, which its home page declares only as an SVG (`https://static.licdn.com/aero-v1/sc/h/akt4ae504epesldzj74dzred8`), rasterized to a PNG at 256x256 on a transparent background with headless Chrome. It is fitted to 256x256 and at most 10 KB, the one file ChatGPT's "Add custom MCP server" form takes as an app's icon (#138). Clients that read `serverInfo` show it beside the service, with its title and website (#135). ChatGPT shows the icon uploaded when its app is added, so upload this file in that form; ChatGPT offers no way to change it on an existing app (openai/openai-apps-sdk-examples#232). To refresh it, fetch the same URL again and fit it the same way (Phase 1 of the `web-api-discovery` skill).
