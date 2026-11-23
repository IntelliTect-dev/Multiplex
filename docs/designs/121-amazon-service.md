# Amazon service: design

Issue: #121. Status: proposed 2026-11-22, waiting on the owner's go-ahead (section 2).
Nothing is built yet.

## 1. What we are building

An `/amazon/mcp` service, so a person in Claude or ChatGPT can search Amazon and get, for
each product, what they need to choose one: price, star rating, number of ratings, what
customers say, the description and the details.

It has two tools:

| Tool | Input | Returns |
|---|---|---|
| `amazon_search` | a query, a page number, a sort order | One results page: for each product its ASIN, title, link, image, price, list and unit price, stars, number of ratings, "bought in past month", badges, delivery text, and whether it is a sponsored ad |
| `amazon_get_product` | an ASIN or an Amazon product link | Everything on the product page that helps a person choose (section 4.2): prices and deal, availability and seller, stars, number of ratings, the star breakdown, Amazon's "Customers say" summary with each aspect's positive and negative counts and quotes, feature bullets, description, details table, Best Sellers Rank, categories, variations, images and returns |

### 1.1 How we got here

- **#28 (2026-11-17/18)** recorded Amazon in a browser. Search results (`/s?k=`) and product
  pages (`/dp/{asin}`) came back only as whole HTML pages. There was no data call for
  web-api-discovery to wrap. The issue was closed, and Amazon waited for HTML support (#69).
- **#59** proposed a generic browser reader, with Amazon as its first use.
- **#96 (2026-11-20)** set the browser rule: no provider uses a browser by default, and any
  browser use needs the owner's approval for that provider and that use case.

### 1.2 The finding that shapes this design (2026-11-22)

#28 assumed that Amazon would refuse plain requests, because its firewall checks the browser.
That was never tested. Four requests from DAKAR, spaced a few seconds apart, tested it. Each
used an honest user agent, with no cookies, no browser headers and no browser:

| Request | User agent | Answer |
|---|---|---|
| `GET /robots.txt` | `Multiplex/0.1 (personal research service)` | `200` |
| `GET /s?k=usb+c+charging+cable` | the same | `200`, 1.6 MB of HTML (291 KB compressed), 22 results. The markup matches the page the browser got in #28 (same `data-component-type`, `data-cy` and `aria-label` hooks) |
| `GET /dp/B088NRLMPV` | the same | `200`, 2.8 MB of HTML (476 KB compressed). Title, price, list price, unit price, 4.7 stars, 91,951 ratings, star breakdown, "Customers say" (eight aspects with counts and quotes), 19 images, details table, Best Sellers Rank |
| `GET /s?k=usb+c+charging+cable` | `Multiplex-Amazon/0.1`, the identity this design ships (section 3) | `200`, 1.3 MB of HTML, 22 results, no robot check |

So **Amazon needs no browser**. The service reads Amazon's pages over plain HTTP and parses
the HTML. Because there is no browser, the #96 rule asks for no approval, and #59 loses its
first use.

That reverses part of #28's conclusion. One quiet day from one home IP is not a guarantee.
The design therefore treats a block as something that will happen (section 5.3).

## 2. Decisions the owner makes before anything is built

**D1. Go ahead at all, given Amazon's terms.** Amazon's Conditions of Use forbid automated
access. The owner knew this when choosing to go ahead in #28. Two newer facts bear on it:

- **robots.txt.** Amazon's `robots.txt` allows `/s` and `/dp/` for `User-agent: *`. However,
  it blocks the user-facing AI fetchers by name with `Disallow: /`: `Claude-User`,
  `Perplexity-User`, `GoogleAgent-Shopping`, `GoogleAgent-Mariner`, `CopilotNative` and
  others. A Multiplex service that fetches pages for a person talking to Claude belongs to
  that class, even though it does not go by any of those names.
- **The Perplexity lawsuit.** In November 2025 Amazon sued Perplexity over its Comet shopping
  agent. The complaint centres on the agent passing itself off as Google Chrome, getting past
  a block Amazon had put up, and acting inside customers' signed-in accounts. Sources:
  [Bloomberg](https://www.bloomberg.com/news/articles/2025-11-04/amazon-demands-perplexity-stop-ai-agent-from-making-purchases),
  [Retail Dive](https://www.retaildive.com/news/amazon-sues-perplexity-ai-shopping-agents/804871/),
  and the [complaint](https://storage.courtlistener.com/recap/gov.uscourts.cand.459191/gov.uscourts.cand.459191.1.0_1.pdf).
  How the case has gone since was not checked.

This design is the most cautious form of the idea:

- it names itself honestly and obeys any robots.txt rule that names it (5.1), so Amazon can
  turn it away with one line;
- it stays signed out and only reads, with no account, cart or purchase;
- it is paced and personal, behind Multiplex's own sign-in allowlist;
- it stops at the first sign of a block and never works around one.

It still breaks the Conditions of Use.

There is one more risk, and it falls on the owner. DAKAR and the Linux host share the
household's public IP address. If Amazon flags that address, the owner's own shopping can
start to hit "I am human" checks too.

**D2. Reviews stay a summary.** Signed out, Amazon's product page says "Sign in to see
customer reviews". It shows no review text, only the stars, the count, the breakdown and
the AI "Customers say" block. That block has each aspect's counts and a few short quotes
from reviews. Individual reviews need an Amazon sign-in, which #59 rules out. This design
returns the summary only.

## 3. Boundaries (carried from #28 and #59, plus #96)

- No browser. Plain HTTP only, so no #96 approval is needed. A browser hand-over for checks
  would be a separate use case that needs its own approval (section 8).
- An honest identity:
  - The user agent is `Multiplex-Amazon/<version>`.
  - No `sec-ch-ua*` client hints and no copied browser headers.
  - No fingerprint tricks and no rotating addresses or proxies.
  - A test enforces this (T3.1).
  - No contact URL in the user agent, because it would name the owner to Amazon. Adding one
    is the owner's choice.
- It obeys robots.txt: the `*` rules, and any group that names `Multiplex-Amazon` or
  `Multiplex` (5.1).
- Nothing answers, solves or gets around a robot check. A check pauses the service (5.3).
- Signed out, read only. No sign-in, account, cart, wish list, address or purchase pages.
  The client follows no redirect outside `/s`, `/dp/` and `/gp/product/`.
- Human-scale pacing: one request in flight to Amazon from the whole host, a few seconds
  apart (5.2).
- `amazon.com` only, in US dollars.

## 4. The tools

### 4.1 `amazon_search`

| Input | Type | Default | Notes |
|---|---|---|---|
| `query` | string, required | | What a person would type into Amazon's search box |
| `page` | int, 1-20 | 1 | Amazon shows about 16 to 22 results per page and stops at page 20 |
| `sort` | `relevance`, `price_low_to_high`, `price_high_to_low`, `avg_customer_review`, `newest`, `best_sellers` | `relevance` | Maps to `s=` `relevanceblender`, `price-asc-rank`, `price-desc-rank`, `review-rank`, `date-desc-rank`, `exact-aware-popularity-rank`, all read from the live sort menu on 2026-11-22 |

Request: `GET https://www.amazon.com/s?k=<query>&page=<n>&s=<sort>`.

Output (field names final; values from the 2026-11-22 page):

```json
{
  "query": "usb c charging cable",
  "page": 1,
  "sort": "relevance",
  "totalResults": "over 60,000",
  "hasMorePages": true,
  "results": [
    {
      "position": 3,
      "asin": "B088NRLMPV",
      "title": "Anker USB C to USB C Cable, 60W Fast Charging Cable (2-Pack, 6 ft, Black)",
      "url": "https://www.amazon.com/dp/B088NRLMPV",
      "imageUrl": "https://m.media-amazon.com/images/I/71k0d1eTetL._AC_UY218_.jpg",
      "price": { "amount": 7.99, "currency": "USD", "display": "$7.99" },
      "listPrice": null,
      "unitPrice": "$4.00/count",
      "rating": 4.7,
      "ratingsCount": 91951,
      "boughtInPastMonth": "10K+",
      "badges": ["Black Friday Deal"],
      "sponsored": false,
      "delivery": "FREE delivery Fri, Nov 27 on $35 of items shipped by Amazon"
    }
  ],
  "warnings": [],
  "source": "https://www.amazon.com/s?k=usb+c+charging+cable",
  "fetchedAt": "2026-11-22T23:52:47Z"
}
```

- **Sponsored results are kept and flagged.** Hiding them would change what the person
  would see on Amazon. The AI can drop them.
- **`url` is built from the ASIN.** Sponsored links go through Amazon's `/sspa/click`
  redirect, and organic links carry tracking. `https://www.amazon.com/dp/{asin}` is stable.
- **A field that can't be read exactly is `null`, never a guess.** One example: `ratingsCount`
  comes from the exact `aria-label` ("91,951 ratings"), never from the shortened "(91.9K)".

### 4.2 `amazon_get_product`

| Input | Type | Notes |
|---|---|---|
| `product` | string, required | An ASIN (`B088NRLMPV`) or any Amazon product link (`/dp/`, `/gp/product/`, `/clp/`, with or without a slug and `ref=`). Anything else is a tool error, and no request is made |

Request: `GET https://www.amazon.com/dp/<asin>`.

Output:

```json
{
  "asin": "B088NRLMPV",
  "title": "Anker USB C to USB C Cable, 60W Fast Charging Cable (2-Pack, 6 ft, Black)",
  "url": "https://www.amazon.com/dp/B088NRLMPV",
  "brand": "Anker",
  "price": { "amount": 7.99, "currency": "USD", "display": "$7.99" },
  "listPrice": { "amount": 9.99, "currency": "USD", "display": "$9.99" },
  "savingsPercent": 20,
  "unitPrice": "$4.00 / count",
  "deal": "Black Friday Deal",
  "coupon": null,
  "availability": "In Stock",
  "soldBy": "AnkerDirect",
  "shipsFrom": "Amazon",
  "delivery": "FREE delivery Friday, November 27 on orders shipped by Amazon over $35",
  "rating": 4.7,
  "ratingsCount": 91951,
  "ratingHistogram": { "5": 87, "4": 8, "3": 2, "2": 1, "1": 2 },
  "boughtInPastMonth": "10K+",
  "badges": ["#1 Best Seller in USB Cables"],
  "bestSellersRank": [{ "rank": 1, "category": "USB Cables" }],
  "categories": ["Electronics", "Computers & Accessories", "...", "USB Cables"],
  "customersSay": {
    "summary": "<Amazon's AI summary paragraph>",
    "aspects": [
      {
        "name": "Quality",
        "mentions": 6150,
        "positive": 5546,
        "negative": 604,
        "summary": "<one-sentence aspect summary>",
        "quotes": ["<short review excerpt>", "<short review excerpt>"]
      }
    ],
    "aiGenerated": true
  },
  "features": ["<feature bullet>", "<feature bullet>"],
  "description": "<product description, else the A+ content text>",
  "descriptionTruncated": false,
  "overview": { "Brand": "Anker", "Connector Type": "USB Type C" },
  "details": { "Item Weight": "3.52 ounces", "Manufacturer": "Anker" },
  "variations": [{ "dimension": "Color", "selected": "Black" }, { "dimension": "Size", "selected": "6FT*2" }],
  "images": ["https://m.media-amazon.com/images/I/71k0d1eTetL._AC_SL1500_.jpg"],
  "returns": "FREE refund/replacement until Jan 31, 2027",
  "warnings": [],
  "source": "https://www.amazon.com/dp/B088NRLMPV",
  "fetchedAt": "2026-11-22T23:52:51Z"
}
```

- `customersSay` is `null` when Amazon shows no summary. Products with few reviews get none
  (the #28 product with 18 ratings had none). That is normal, not an error.
- `description` is capped at 4,000 characters, and `descriptionTruncated` says when it was
  cut. A+ content can run much longer.
- `variations` gives the selected value of each dimension only. Listing every option and its
  price is later work (section 8).
- `fetchedAt` is on every answer. Prices and deals change by the hour, so the AI can say
  "as of".

### 4.3 Where each field comes from

These are the hooks found on 2026-11-22. They are knowledge about Amazon's pages, so they
live in the page classes (section 6). Each row is pinned by a T1 test against a fixture
(section 7). A hook marked *(few-ratings page)* was seen only on the #28 product (`B0FFK2WQJH`);
the rest are on the 2026-11-22 popular product (`B088NRLMPV`).

| Field | Search page (per `div[data-component-type="s-search-result"][data-asin]`) | Product page |
|---|---|---|
| ASIN | `data-asin` | `input#ASIN` value |
| Title | `h2` `aria-label`, without its `Sponsored Ad - ` prefix (the text also holds badge words like "ClimatePartner Verified") | `#productTitle` |
| Price | first `.a-price:not(.a-text-price) .a-offscreen` | `#corePrice_feature_div .a-price .a-offscreen` (on some layouts, `#apex_desktop`) |
| List price | `.a-price.a-text-price[data-a-strike="true"] .a-offscreen` | `.basisPrice .a-offscreen` |
| Unit price | the `.a-text-price` that is **not** struck through ("$4.00/count") | `.pricePerUnit` |
| Stars | `[aria-label$="out of 5 stars, rating details"]` | `#acrPopover[title]` |
| Ratings count | `aria-label` "91,951 ratings" | `#acrCustomerReviewText` "(91,951)" |
| Histogram | | `#histogramTable a[aria-label]` "87 percent of reviews have 5 stars" |
| Customers say | | `[data-testid="overall-summary"]`; aspects from `#rh_controls_aspect_N` (`data-testid="bottomsheet-content-<aspect>"`), whose text is "6150 customers mention quality, 5546 positive, 604 negative", plus `aspect-summary` and the quotes in each aspect |
| Bought in past month | text "... bought in past month" | `#social-proofing-faceout-title-tk_bought` |
| Badges | `.a-badge-text`, `[data-cy="s-pc-faceout-badge"]` | `#acBadge_feature_div`, `#zeitgeistBadge_feature_div` ("#1 Best Seller in ...") |
| Sponsored | `h2[aria-label^="Sponsored Ad"]`, `.puis-sponsored-label-text` | |
| Image | `img.s-image[src]` | `'colorImages': { 'initial': A.$.parseJSON('[...]') }` in the `ImageBlockATF` script, each item's `hiRes` |
| Features, description | | `#feature-bullets li`; `#productDescription`, else `#aplus_feature_div` |
| Details | | `#productOverview_feature_div tr`, `table.prodDetTable tr` (th/td), `#detailBullets_feature_div li` *(few-ratings page)* |
| Availability, seller | | first text of `#availability` (the element also holds inline JSON); `#merchantInfoFeature_feature_div` ("Sold by AnkerDirect", or "Shipper / Seller Amazon.com") |
| Categories | | `#wayfinding-breadcrumbs_feature_div li a` |
| Totals, paging | `[data-component-type="s-result-info-bar"]`, `a.s-pagination-next` | |

Traps found in the real pages; each becomes a test (T1):

- The unit price uses the same `.a-text-price` class as the list price. Only the struck-through
  one is the list price. A quick first parse got this wrong.
- The visible count is shortened ("(91.9K)"). The exact count is only in the `aria-label`.
- The deal badge holds template text ("NO_OF_HOURS hours NO_OF_MINUTES minutes"), which is
  dropped.
- The `canonical` link is `/clp/{asin}`, not `/dp/`, so URLs are built from the ASIN.
- The byline reads "Brand: Amazon" on one page, "Visit the Anker Store" on another, and is
  missing on a third. There, `Brand Name` in the details table is used.
- Some pages put details in a table (`prodDetTable`), others in a bulleted list
  (`detailBullets`). The parser reads both.

## 5. How a call works

A simplified flow. The one retry, redirects and the second check at the front of the queue
are in 5.2 and 5.3.

```mermaid
flowchart LR
    AI[Claude / ChatGPT] -->|tools/call| Tool[AmazonTools]
    Tool --> Cache{cached < 10 min?}
    Cache -- yes --> Tool
    Cache -- no --> Breaker{paused after a block?}
    Breaker -- yes --> Err[tool error: paused until hh:mm]
    Breaker -- no --> Robots{robots.txt allows it?}
    Robots -- no --> Err3[tool error: robots.txt refuses it]
    Robots -- yes --> Pacer[gate: one at a time, 3-5 s apart, no retries]
    Pacer --> Http[HttpClient, honest UA, no cookies]
    Http --> Classify{classify answer}
    Classify -- page --> Parse[AmazonSearchPage / AmazonProductPage]
    Classify -- robot check --> Pause[open breaker] --> Err2[tool error: Amazon refused]
    Classify -- 404 --> NF[tool error: no such product]
    Parse -- anchors missing --> LC[tool error: layout changed]
    Parse -- ok --> Tool
```

### 5.1 The request

- A named `HttpClient` ("amazon") on `SocketsHttpHandler`, like LinkedIn and TripIt:
  `UseCookies = false`, `AllowAutoRedirect = false`, gzip and Brotli on, and a 30-second
  timeout.
- Headers: `User-Agent: Multiplex-Amazon/<assembly version>`, `Accept: text/html`,
  `Accept-Language: en-US`. Nothing else.
- Redirects are read, not followed blindly. A redirect to a `/dp/` or `/gp/product/` page is
  followed once. A redirect to `/ap/signin`, `/errors/validateCaptcha` or anywhere else
  counts as a block (5.3).
- No cookies are kept. Every request looks like a new, signed-out visitor, which is what the
  service is. Keeping Amazon's `session-id` cookie would make requests linkable, and the
  stateless-server rule argues against holding it anyway. The live tests will show whether
  Amazon minds. If it does, that becomes its own decision.
- **robots.txt is obeyed for the service's own name.** The client fetches `/robots.txt` at
  most once a day, through the same gate, and keeps the parsed rules in memory. Before every
  request it checks the path against:
  - the group that names `Multiplex-Amazon` or `Multiplex`, if there is one;
  - otherwise the `*` group.

  A disallowed path fails the call without contacting Amazon ("Amazon's robots.txt does not
  allow this service to read /s"). The same check, and the same refresh-first rule below,
  apply to a redirect's target.

  The parser follows RFC 9309, because Amazon's own file needs it. It handles:
  - several `User-agent` lines sharing one group, matched without regard to case;
  - comments;
  - `*` anywhere in a path and `$` at its end (Amazon's `*` group uses both, for example
    `/gp/registry/wishlist/*/reserve`);
  - the longest match winning, with `Allow` winning a tie.

  The robots.txt fetch goes through the gate and the classifier like any other request, so
  a block on it pauses the service. A copy older than a day is refreshed **before** the call
  joins the queue for its own request: the refresh takes a turn of its own, and calls that
  arrive during it wait for the same refresh rather than starting another. The check at the
  front of the queue (5.2) only reads the copy in memory and never fetches, so a call never
  waits on the gate while holding its turn. A `404` means everything is allowed, as RFC 9309 says.
  For a `5xx` or a timeout, the last copy is used; with no copy, the call fails.

  That makes "Amazon can turn it away with one line" true, not just stated. The service does
  **not** apply the groups for other agents (`Claude-User` and the rest). Whether those
  should bind it is D1.

### 5.2 Pacing: a gate of its own, with no retries inside it

**Why not reuse `PacingHandler`.** TripIt's `PacingHandler` (`tripit/src/TripIt/Http/`)
retries every `429` and `503` GET before the caller sees the answer. Amazon's "automated
access" refusal is a `503`, so the handler would fetch it up to three more times, 1 to 4
seconds apart. That is exactly what section 3 rules out. The handler also has no limit on how
long a call waits in its queue.

So the service gets a small gate of its own. It is new code, under 100 lines, in
`HtmlSites/SiteGate.cs`, and has an injected `TimeProvider` and random source for tests.

- **One at a time for the whole host:** one request in flight to Amazon, the robots.txt
  fetch included. Amazon sees one IP, however many people use Multiplex.
- **Spacing:** at least 3 seconds after the previous answer finished, plus up to 2 seconds
  of random jitter, so at most about 15 a minute. That is close to web-api-discovery's human
  pace of 12.
- **Queue limit:** a call that would wait more than 30 seconds for its turn fails at once:
  "Amazon requests are queued; try again in a moment". An AI that fires ten product lookups
  in parallel gets fast errors, not a ten-minute hang.
- **Volume caps:** at most 100 requests to Amazon in any hour and 500 in any day, counted
  over a rolling window. Every request counts, including robots.txt fetches, retries and
  followed redirects. Over a cap, a call fails at once and says when the window frees up.
  The caps turn "low volume" in D1 and section 9 into a limit rather than a hope.
- **Checked again at the front of the queue.** When a call's turn comes, the gate hands it
  back to `AmazonClient`. The client checks the pause (5.3) and robots.txt again before
  sending, because a call can sit in the queue while the one ahead of it meets a robot check.
- **No retries in the gate.** Every answer goes to the classifier (5.3) first.
  `AmazonClient` then retries **once**, through the gate again, and only when:
  - the answer was a `429` carrying a `Retry-After` of 60 seconds or less; or
  - it was `Unavailable` (a `5xx` without the block markers), with a `Retry-After` of 60
    seconds or less, or none, in which case the retry waits 5 seconds.

  A `429` with no `Retry-After` is a throttling signal with no wait named, so it pauses the
  service like a block, but it is not one. Anything else is the call's answer. That includes
  a second failure, and a `Retry-After` over 60 seconds ("Amazon asked for a wait until
  14:30 UTC; try again then"). A block is never retried.
- **Redirects go back through everything:** the gate, the spacing, the pause and the
  robots.txt check, like a new request.
- The numbers are constants, not settings. Changing them is a code change. This adds no new
  option, which keeps the "prompt first" rule for options out of play.

### 5.3 Blocks: recognise, stop, say so

`AmazonPageClassifier.Classify(status, headers, requestUrl, location, body)` is a pure
function with one of these results:

| Result | Recognised by | What happens |
|---|---|---|
| `Page` | `200` and none of the markers below | Parse |
| `NotFound` | `404`, or Amazon's "Sorry! We couldn't find that page" | Tool error naming the ASIN |
| `RobotCheck` | Any status with a form posting to `/errors/validateCaptcha`, or the text "Enter the characters you see below" / "make sure you're not a robot" | **Pause**, tool error |
| `AutomatedAccessRefused` | The page that says "To discuss automated access to Amazon data please contact api-services-support@amazon.com" (usually `503`) | **Pause**, tool error |
| `Challenge` | `202` with `x-amzn-waf-action`, the firewall's script check that only a browser can pass | **Pause**, tool error |
| `SignInWall` | A redirect to `/ap/signin` | **Pause**, tool error |
| `Redirect` | A redirect to another `/dp/` or `/gp/product/` page on `www.amazon.com` | Followed once; a second redirect is a tool error |
| `Blocked` (other) | A redirect anywhere else, on Amazon or off it | **Pause**, tool error; never followed |
| `Throttled` | `429` | With a `Retry-After` of 60 seconds or less, one retry (5.2), then a tool error. Without one, **pause**, tool error |
| `Unavailable` | `5xx` without the markers above | One retry (5.2), then a tool error |

The status alone is not enough. A robot check can arrive with a `200`, so the markers are
checked on every answer, whatever its status.

**The pause** is a circuit breaker for the whole service. It lives in `AmazonClient` and
reads time from an injected `TimeProvider`. The first block pauses every Amazon request for
15 minutes. Each later block doubles the pause, up to 2 hours. A success resets it. While
paused, calls fail at once, without contacting Amazon:

> Amazon refused this kind of request (it showed a robot check at 14:05 UTC). Multiplex does
> not answer those checks. Amazon requests are paused until 14:20 UTC.

The pause lives in the process, not in any MCP session, so the stateless-server rule holds.
A second instance would keep its own pause, which only makes it more cautious.

Nothing ever answers the check. Letting a person answer one in a browser is the #59 idea. It
needs a browser, so it needs its own #96 approval (section 8).

### 5.4 Layout changes: a named error, never a guess

Each page class checks its **anchors** before it reads any field:

- **Search:** at least one `div[data-component-type="s-search-result"][data-asin]`, or
  Amazon's "No results for" message. The message gives an empty `results` list, not an
  error.
- **Product:** `#productTitle` and an ASIN.

If an anchor is missing on a page the classifier called a `Page`, the call fails:

> Amazon's page layout has changed: the search results list was not found. The Amazon
> service needs updating.

The log records which anchor was missing, with the status and the page size. The server
keeps no copy of the page: that would be disk state, and there is nowhere settled to put
it. A developer gets the failing page by running the live tests, which save it to the
gitignored `Recorded/` folder (T5).

Below the anchors, every field is optional, and a missing field is `null`. A bulk loss is
still worth flagging, so it goes in `warnings` and the log. For example: a search page with
five or more results where no result has a price, or none has a rating. Another: a
histogram whose percentages don't add up to 97-103. That catches a renamed price class
while the results list itself still parses.

### 5.5 Cache

Parsed answers are cached in memory for 10 minutes, keyed by request URL, up to 200 entries,
with the oldest evicted first. The cache reads time from the same injected `TimeProvider`,
so tests can expire it. Calls for the same URL at the same time share one fetch: the second
waits for the first's answer rather than queueing a second request. An AI often looks up the same product more than once in one conversation.
Every request saved is less load on Amazon and less risk of a block. `fetchedAt` keeps the
time of the real fetch, so a cached answer never claims to be newer than it is.

### 5.6 Logging

The log records the outcome, the HTTP status, the classification, the ASIN, the elapsed time
and the time spent waiting for the pacer. It never records page bodies or search text,
because what someone searches for can be personal.

## 6. Code layout

| Path | What it holds |
|---|---|
| `src/Multiplex.Web/Services/Amazon/AmazonTools.cs` | `[McpServerToolType]`, `[McpEndpoint("amazon")]`: the two tools; input checks; turning failures into `McpException` |
| `.../Amazon/AmazonClient.cs` | URL building, robots.txt checks, the cache, the breaker, the one retry, calling the classifier and the parsers. Takes a `TimeProvider` |
| `.../Amazon/AmazonSearchPage.cs` | Search-page knowledge: anchors, selectors, parsing to `AmazonSearchResults` |
| `.../Amazon/AmazonProductPage.cs` | Product-page knowledge, parsing to `AmazonProduct` |
| `.../Amazon/AmazonPageClassifier.cs` | The table in 5.3 |
| `.../Amazon/AmazonModels.cs` | The output records (section 4) |
| `.../Amazon/AmazonServiceRegistration.cs` | `AddAmazonService()`: the named client, the gate, the cache |
| `.../Amazon/README.md` | The service README, laid out like Time's |
| `src/Multiplex.Web/HtmlSites/SiteGate.cs` | The gate in 5.2: one in flight, spacing with jitter, the 30-second queue limit, the hourly and daily caps, no retries |
| `src/Multiplex.Web/HtmlSites/RobotsRules.cs` | Parsing robots.txt and checking a path for a named agent (longest match, `Allow` beats `Disallow` on a tie) |
| `src/Multiplex.Web/HtmlSites/CircuitBreaker.cs`, `SiteErrors.cs`, `PageAnchors.cs` | The pause, the "blocked" and "layout changed" errors, the anchor check |
| `Program.cs` | `.RequireAuthFor("amazon", "Amazon", needsSiteAccount: false)` and `AddAmazonService()` |
| `tests/Multiplex.Tests/ChromiumFactAttribute.cs` | A `[LiveHttpFact]` beside `[LiveFact]`: needs `MULTIPLEX_LIVE_TESTS=1` but not Chromium. The existing variable, so no new option |
| `.gitignore` | `tests/Multiplex.Tests/Services/*/Recorded/` |
| `spec/product.md` | One catalog row |
| `.github/instructions/project.instructions.md` | Amazon joins Time as an exception to "call the site's API, not its pages", because it has no data calls (#28). Unlike Time, it uses no browser |

**HTML parsing: add AngleSharp.** The web project has no HTML parser today. Time evaluates
JavaScript in Chromium, and LinkedIn uses regular expressions. Neither fits: this service has
no browser, and 1.3 to 2.8 MB of Amazon markup is no place for regular expressions.
AngleSharp is MIT-licensed, pure .NET, and supports the CSS selectors in 4.3 directly. It is
the only new package.

**Generic, but only as far as this needs.** The owner asked for a generic answer to page-only
sites in #59. Five pieces here know nothing about Amazon:

- the gate;
- the robots.txt rules;
- the circuit breaker;
- the "layout changed" and "site blocked" errors;
- the anchor check.

They go in `src/Multiplex.Web/HtmlSites/`, not `Services/Amazon/` (not `Pages/`, which
belongs to Razor). Everything that knows Amazon stays in `Services/Amazon/`. The next page-only
site (Wirecutter's reviews, #68 and #69) then starts with those five pieces. Nothing more is
built ahead of need, so there is no site-description format and no generator.

## 7. How it is tested

The repository is **public**, so the tests and fixtures follow a fixture policy (7.1). There
are six layers. The first four run on every `dotnet test`, with no network and no Chromium.

### 7.1 Fixtures

They live in `tests/Multiplex.Tests/Services/Amazon/Fixtures/`. The csproj already copies
`Services\*\Fixtures\**` to the output folder.

**Policy, because the repository is public:**

- A committed fixture is a **trimmed** Amazon page. It keeps the elements the page classes
  read, with their real ids, classes, `data-*` and `aria-*` attributes, and their facts:
  prices, stars, counts, ASINs and short titles.
- It replaces Amazon's prose with short placeholders: descriptions, A+ content, the "Customers
  say" summaries and review quotes. That text is Amazon's or its customers' work, and a
  public repository is no place for it.
- It drops scripts, styles, images and Amazon's per-visitor ids (`ue_sid`, `session-id`
  values). The one script kept is the `colorImages` line, cut to two images.
- Whole real pages never get committed. They live in a gitignored
  `tests/Multiplex.Tests/Services/Amazon/Recorded/` folder, or in the `.har-captures/` store.
  Layer T6 reads them if they are there.
- A small trimming helper in the test project produces the fixtures. Its keep-list is built
  from the same selector constants the page classes use, so the fixtures and the parser
  cannot drift apart. A guard test (T1.12) fails if a fixture carries a visitor id or a long
  run of prose.

**The fixture set**, each named for what it proves:

| Fixture | From | Proves |
|---|---|---|
| `search-results.html` | the 2026-11-22 plain-HTTP search | organic and sponsored results, badges, list versus unit price, shortened counts, paging |
| `search-no-results.html` | to record | "No results for" gives an empty list, not a layout error |
| `product-popular.html` | the 2026-11-22 `B088NRLMPV` page | Customers say with 8 aspects, histogram, deal, list and unit price, variations, details table, Best Sellers Rank, 19 images |
| `product-few-ratings.html` | the #28 capture of `B0FFK2WQJH` | no Customers say gives `null`; "Brand: Amazon" byline; details as a bulleted list |
| `product-unavailable.html` | to record | "Currently unavailable" gives a `null` price and the availability text |
| `product-no-ratings.html` | to record | a new product with no ratings gives a `null` rating and count, not an error |
| `product-book.html` | to record | a book's layout (author byline, formats): at least title, price, rating and description |
| `blocked-robot-check.html` | made by hand from Amazon's known check form, because it can't be triggered on purpose | `RobotCheck` |
| `blocked-automated-access.html` | made by hand, the same way | `AutomatedAccessRefused` |
| `not-found.html` | to record | `NotFound` |
| `robots.txt` | Amazon's 2026-11-22 file, cut to its `*` group and a few named AI groups | the real rules allow `/s?k=` and `/dp/`, and the wildcard and `$` rules parse |

"To record" means fetching that page once, through the service's own client (paced, honest),
while the service is being built.

### 7.2 Layers

**T1. Page parsing.** These are pure functions over fixtures, the bulk of the tests. Names
say the behaviour:

1. `Search_ReadsAsinTitlePriceStarsAndCount_ForEachResult`
2. `Search_FlagsSponsoredResults_AndStripsTheSponsoredPrefixFromTitles`
3. `Search_TakesTheExactRatingsCount_NotTheShortenedText` (91951, not 91.9K)
4. `Search_ReadsListPriceFromTheStruckThroughPrice_NotTheUnitPrice`
5. `Search_BuildsProductUrlsFromTheAsin_NotFromTrackingLinks`
6. `Search_WithNoResults_ReturnsAnEmptyList`
7. `Product_ReadsCustomersSay_WithEachAspectsPositiveAndNegativeCounts`
8. `Product_WithoutCustomersSay_ReturnsNullSummary`
9. `Product_ReadsTheHistogram_AndItAddsUpToAbout100`
10. `Product_ReadsDetailsFromTheTable_OnThePopularPage_AndFromTheBullets_OnTheFewRatingsPage`
11. `Product_ReadsHiResImagesFromTheGalleryData`
12. `Fixtures_CarryNoVisitorIdsOrLongProse` (the policy guard). For it and the trimming helper
    to share the keep-list, the page classes expose their selectors as `internal` constants
    (the test project already sees internals).
13. `Search_WithoutTheResultsList_FailsWithLayoutChanged_NamingIt`: a fixture with the list
    removed must give a named error, not an empty list.
14. `Search_WherePricesVanishFromEveryResult_AddsAWarning`
15. `Product_WhoseHistogramDoesNotAddUp_AddsAWarning`
16. `Product_DropsTheDealBadgesTemplateText` ("NO_OF_HOURS")
17. `Product_BuildsItsUrlFromTheAsin_NotTheClpCanonical`

**T2. Classifier.** Table-driven over status, headers, location and body:

- a `200` that is really a robot check gives `RobotCheck`;
- the `503` automated-access page gives `AutomatedAccessRefused`, and a plain `503` gives
  `Unavailable`;
- a `302` to `/ap/signin` gives `SignInWall`;
- a `202` with `x-amzn-waf-action` gives `Challenge`;
- a `404` gives `NotFound`;
- a `429` gives `Throttled`;
- a `301` to another `/dp/` page gives `Redirect`, while a `301` to `/gp/cart`, to another
  Amazon host or off Amazon gives `Blocked`.

robots.txt rules are tested here too, against small hand-written files:

- `*` disallowing `/s` refuses a search;
- a group naming `Multiplex-Amazon` wins over `*`;
- a group naming only `Claude-User` is ignored, as 5.1 says;
- for a path, the longest match wins, and `Allow` beats `Disallow` on a tie;
- `*` matches any run of characters (`/gp/registry/wishlist/*/reserve`), and `$` anchors
  the end (`/slp/s$` refuses `/slp/s` but not `/slp/sale`);
- several `User-agent` lines share one group, and comments are ignored;
- the `robots.txt` fixture's `*` group allows `/s?k=...` and `/dp/B088NRLMPV`.

Input checks are in this layer too:

- an ASIN in any link shape is extracted;
- `B088NRLMPV` and a link that holds it give the same request;
- free text, a 9-character id and a non-Amazon host are refused, and no request is made;
- query encoding (`&`, `+`, quotes, non-ASCII), sort mapping and page bounds are right.

**T3. Client behaviour.** This uses a fake `HttpMessageHandler`, the pattern of
`FakeLinkedInHttp` and `FakeTripIt`. It also uses a fake `TimeProvider` and a fixed random
source, shared by the gate, the breaker and the cache, so no test waits in real time:

1. `SendsAnHonestIdentity`: the user agent starts with `Multiplex-Amazon/` and has no
   `Mozilla`, and there is no `sec-ch-ua*`, no `Cookie` and no `Referer`. Copying a browser's
   headers in later fails this test. That is the boundary in section 3, written as a test.
2. `SpacesRequests_OneAtATime`: two calls at once reach the handler at least 3 seconds
   apart.
3. `ACallThatWouldWaitOver30Seconds_FailsAtOnce`, without reaching the handler.
4. `TheAutomatedAccess503_IsFetchedOnce_AndNeverRetried`, the review finding that ruled out
   reusing `PacingHandler`. The same holds for each block kind.
5. `ARobotCheckPausesTheService_WithoutContactingAmazonAgain`: after one blocked answer, the
   next call fails without reaching the handler. After 15 minutes, one call goes through.
6. `ACallQueuedBehindABlock_FailsWithoutReachingAmazon`: two calls queue, the first meets a
   robot check, and the handler is never called for the second.
7. `RepeatedBlocksDoubleThePause_UpTo2Hours`, then `ASuccessResetsIt`.
8. `A429WithRetryAfter_IsRetriedOnce`, `ASecond429_IsTheAnswer`, and
   `A429WithoutRetryAfter_PausesTheService`.
9. `ARetryAfterOver60Seconds_IsNotWaitedOut_AndTheErrorNamesTheTime`.
10. `APlain503_IsRetriedOnce_ThenReportedAsUnavailable`.
11. `ARedirectToAnotherProductPage_IsFollowedOnce_BackThroughTheGateAndRobotsCheck`, and
    `ASecondRedirect_IsAnError`.
12. `ARedirectOutsideProductPages_IsTreatedAsABlock_AndNotFollowed`, on and off Amazon.
13. `ASecondLookupWithin10Minutes_IsServedFromCache`: one request, and `fetchedAt` is
    unchanged. `After10Minutes_ItFetchesAgain`.
    `TheCacheHoldsAtMost200Answers` (the oldest entries are evicted first).
    `TwoCallsForTheSameProductAtOnce_ShareOneFetch`.
14. `APathRobotsTxtDisallows_IsRefused_WithoutARequest`,
    `RobotsTxtIsFetchedAtMostOnceADay`, `ABlockOnRobotsTxt_PausesTheService`, and
    `AStaleRobotsTxt_IsRefreshedBeforeQueueing_AndTheCallStillSends` (no deadlock: the
    handler sees robots.txt, then the page).
15. `TheHourlyAndDailyCaps_RefuseCalls_UntilTheWindowFreesUp`, counting robots.txt fetches,
    retries and redirects.
16. `TheLogNeverHoldsSearchTextOrPageBodies`: a capturing logger runs a search for a
    distinctive phrase, and nothing logged contains it or any page markup.

**T4. Through MCP.** This uses `WebApplicationFactory<Program>`, with the fake handler serving
fixtures, and the JSON-RPC client the TripIt host tests use:

- `tools/list` on `/amazon/mcp` lists exactly `amazon_search` and `amazon_get_product`, and
  neither appears on any other endpoint;
- `tools/call amazon_search` returns the section 4.1 shape;
- a robot-check fixture gives `isError: true` with the pause message;
- an unknown ASIN gives a tool error naming it;
- `UsesNoBrowser`: the Amazon tool methods take no `BrowserService` or `SiteBrowserManager`.
  It is a small reflection test that keeps the #96 rule true for this service.

**T5. Live.** Opt-in with `MULTIPLEX_LIVE_TESTS=1`, against the real amazon.com. It uses the
new `[LiveHttpFact]`, which does not require Chromium, since this service uses none.

- **At most four requests a run, paced:** robots.txt, one search ("usb c cable"), the
  popular product, one book.
- **It checks invariants, not values:**
  - at least 10 results;
  - at least 80% of results with a price, stars and an exact count;
  - the popular product has a non-null `customersSay` with at least 3 aspects, and a
    histogram adding up to 97-103;
  - the book has a title, price and description.
- On failure it saves the fetched page into the gitignored `Recorded/` folder for diffing.
- **When it runs:** before each Amazon PR merges (dev loop functional test), and whenever
  the service starts failing. A scheduled weekly run on DAKAR would catch layout changes
  sooner. It is not part of this design, and would need the owner's say-so.

**T6. Recorded pages.** These run only when `Recorded/` holds pages. No new switch is needed,
because the folder's presence is the signal. Every recorded page goes through its parser,
which must not report a changed layout, and the T5 invariants must hold. Refreshing the
committed fixtures starts here.

### 7.3 Acceptance by hand

Run the host and connect Claude Code to it. A service that acts for no one needs no
sign-in from the host itself.

```bash
dotnet run --project src/Multiplex.Web --launch-profile http
```

```bash
claude mcp add --transport http amazon http://localhost:5190/amazon/mcp
```

Then ask, and check the answers against amazon.com in a normal browser:

1. "Find a USB-C to USB-C cable under $10 with at least 4.5 stars, and tell me what
   customers complain about." Expect `amazon_search`, then `amazon_get_product` on one or two
   results, with an answer citing price, stars, count and the negative aspects.
2. "Compare B088NRLMPV and B09LCJPZ1P." Expect two product calls, with the second spaced from
   the first.
3. "Look up Amazon product B000000000." Expect a clear not-found message.
4. Ask the same product question twice. The second answer comes from the cache, with the
   same `fetchedAt`.

Then repeat 1 from claude.ai through the Linux host, once the service is deployed there.
DAKAR's copy stays on its pinned build, so it is not the place for this.

## 8. Not in this design (later, each its own issue if wanted)

- **Individual reviews:** needs an Amazon sign-in (D2).
- **All offers and other sellers:** Amazon loads these separately (`aodAjaxMain`).
- **Search filters:** price range, minimum stars, Prime, department. Amazon's `rh=` filter
  ids vary by category. Each one would need a recording to confirm before adding a tool
  input. The AI can filter by the price and stars it gets back meanwhile.
- **Every variation option with its price.**
- **Search-as-you-type suggestions.** This is Amazon's one clean JSON call (#28). It is
  little use to an AI, which writes its own queries.
- **Other marketplaces** (`.co.uk`, `.de`, ...).
- **A person answering a robot check in a browser** (#59, using the TripIt mirror). It would
  need the owner's #96 approval for "Amazon: answer a robot check". The mirror code is
  private to sign-in today, so it would first need pulling out.
- **Generated HTML extractors from web-api-discovery** (#69). This design's hand-written page
  classes are the "hand-written extension point" answer #69 asked about.

## 9. Risks

| Risk | What reduces it |
|---|---|
| Amazon's terms and robots.txt (D1) | An honest identity, signed out, read only, personal use, low volume, stopping at any block |
| The household IP gets flagged, and the owner's own shopping meets robot checks | Pacing, the pause, the cache, and hard caps of 100 requests an hour and 500 a day |
| Markup changes | Named layout errors, bulk-loss warnings, live tests that save the failing page |
| Answers reflect the host's location (delivery dates, sometimes prices) and a signed-out view (no Prime pricing, no personalisation) | Delivery is returned as Amazon's own text; README says whose view it is |
| Prices change hourly | `fetchedAt` on every answer; a 10-minute cache |
| Sponsored results mislead | `sponsored: true` on each one |

## 10. Build order (test-first, one PR)

1. The classifier, its table, and the robots.txt rules (T2), with hand-made block fixtures.
2. `AmazonSearchPage`, with the `search-results.html` fixture (T1 1-6, 13-14).
3. `AmazonProductPage`, with both product fixtures, then the recorded ones (T1 7-12, 15-17).
4. `SiteGate`, then `AmazonClient`: identity, gate, robots check, pause, the one retry,
   cache, redirects, logging (T3).
5. The tools, registration, catalog row, README and instruction note (T4).
6. Live tests (T5), the hand acceptance (7.3), the independent review by a non-author
   model, then the PR that closes #121.
