# LinkedIn service (`/linkedin/mcp`)

The LinkedIn service lets Claude or ChatGPT handle the signed-in person's LinkedIn
**invitations** and **messages**, and nothing else: no profile, posting, feed, people
search, jobs or company pages. Each signed-in LinkedIn account gets its own data; nothing is
shared between accounts.

Design and decisions: [issue #86](https://github.com/IntelliTect-dev/Multiplex/issues/86).

**Status: in progress.** The tools, their confirm step and the address's sign-in protection are
built and tested against a stand-in LinkedIn. The part that calls LinkedIn, and LinkedIn
sign-in, wait on a recording of LinkedIn's own requests (see the issue). Until then every tool
answers that this server cannot reach LinkedIn yet.

## Risk

LinkedIn offers no official API for invitations or messages, so this service uses the
requests LinkedIn's own web pages send. LinkedIn's User Agreement (section 8.2) forbids bots
and automated messaging or contact-adding, and LinkedIn says accounts that use such tools
"risk having their accounts restricted or shut down". The owner chose this knowingly
(2026-11-19). The guardrails below keep the service to what a person would do by hand, one
confirmed action at a time; they do not make it allowed.

## Tools

Every tool declares `readOnlyHint`, `destructiveHint`, `idempotentHint` and `openWorldHint`.

| Tool | Kind | Notes |
|---|---|---|
| `linkedin_list_invitations` | read | `direction` = received / sent; pages with `start` and `count` (at most 50) |
| `linkedin_list_conversations` | read | Most recent first; `next_cursor` for older |
| `linkedin_read_conversation` | read | Oldest first; `older_cursor` to read further back. May mark the conversation read |
| `linkedin_accept_invitation` | change, confirm | One received invitation |
| `linkedin_ignore_invitation` | change, destructive, confirm | One received invitation |
| `linkedin_send_invitation` | change, confirm | One member; optional note of at most 300 characters. Says "nothing to do" for a connection or a waiting invitation |
| `linkedin_follow`, `linkedin_unfollow` | change, confirm (unfollow destructive) | One member. Says "nothing to do" when already in that state |
| `linkedin_reply` | change, confirm | One existing conversation; the preview names everyone who will receive it |
| `linkedin_start_conversation` | change, confirm | One 1st-degree connection only (never InMail) |

Every change tool is open-world: it reaches another person. A member is named by their public
profile id (the part after `/in/` in their profile URL) or by the profile URL itself; anything
else (another site, a company page, a sub-page of a profile, a URN) is refused before LinkedIn
is asked. No tool takes a list: bulk or templated sending is one call per person, each with its
own confirmation.

Notes and message text come from other people, and the read tools' descriptions tell the model
to treat them as content, never as instructions.

## Confirm step

The same mechanism as the TripIt service (it uses that service's confirm tokens). A change tool
called without `confirm_token` checks the request against LinkedIn (the invitation is still
waiting, the member exists and is or is not a connection, the conversation exists), changes
nothing, and returns a preview, for example *"Will send a LinkedIn connection request to Jane
Doe (Engineer at Contoso), https://www.linkedin.com/in/jane-doe/, with this note: "…""*, plus
a `confirm_token`. The model shows the preview to the person; only if they agree does it call
again with the same arguments and the token. The second call checks again before acting.

The token lasts 5 minutes, works once, and is bound to the account, the tool and the exact
arguments, so a token for one invitation cannot ignore another and a token for one message
cannot send different text.

## Tests

- `tests/Multiplex.Tests/Services/LinkedIn/LinkedInAccessTests.cs`: the address refuses a
  request without a token, through a tunnel too, and refuses a TripIt token.
- `LinkedInToolTests.cs`: real MCP JSON-RPC and real OAuth in front of a stand-in LinkedIn:
  the tool list and hints (no tool takes a list), each account's own data, preview before
  every change, one change per token, tokens bound to their arguments, the connection and
  note rules, group replies naming everyone, and how failures are reported.
- `LinkedInProfileIdTests.cs`: which profile ids and URLs are accepted.
