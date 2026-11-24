# Amazon service (`/amazon/mcp`)

The Amazon service lets Claude or ChatGPT search amazon.com and read a product page, so a
person gets what they need to choose a product: price, stars, number of ratings, what
customers say, the description and the details. It reads Amazon signed out, over plain
HTTPS, and uses no browser.

Design and decisions: [docs/designs/121-amazon-service.md](../../../../docs/designs/121-amazon-service.md)
and [issue #121](https://github.com/IntelliTect-dev/Multiplex/issues/121).

## Risk

Amazon's Conditions of Use forbid automated access, and its robots.txt refuses AI
assistants' fetchers by name. The owner chose to go ahead knowing this (2026-11-22). The
service is the most cautious form of the idea, and it still breaks those terms:

- It names itself honestly (`Multiplex-Amazon/<version>`) and copies nothing from a browser.
- It obeys any robots.txt rule that names it, and the rules for everyone.
- It stays signed out and only reads. It never opens a sign-in, cart or account page.
- It sends one request at a time, a few seconds apart, at most 100 an hour and 500 a day.
- It stops at any block and pauses itself. A robot check is answered by a person, never by
  the service.

DAKAR and the Linux host share the household's public address. If Amazon flags it, the
owner's own shopping can start to meet "prove you're human" checks.

## Tools

| Tool | What it does |
|---|---|
| `amazon_search` | Searches and returns up to 10 pages of results, sorted, with ads dropped |
| `amazon_get_product` | Reads one product page, and returns its main image as an image |

### `amazon_search`

| Input | Default | Notes |
|---|---|---|
| `query` | required | As typed into Amazon's search box |
| `sort` | `relevance` | `relevance`, `price` or `rating` (see below) |
| `pages` | 1 | 1 to 10 result pages in one call, about 4 seconds each |
| `startPage` | 1 | 1 to 20. `nextPage` in the answer says where to carry on |
| `minPrice`, `maxPrice` | none | US dollars |
| `minRating` | 3 (4 for `price`) | 3 to 5. Nothing rated below 3 stars is ever returned |
| `minRatingsCount` | 20 for `price` and `rating` | Drops products with fewer ratings |
| `filters` | none | Codes from `availableFilters` in an earlier answer, one per group, at most 10 |

**The three sorts.** Each asks Amazon for its own order, then sorts what came back:

| `sort` | Asks Amazon for | Then | Minimums it adds |
|---|---|---|---|
| `relevance` | Featured | Amazon's order | none beyond 3 stars |
| `price` | Price: low to high | cheapest first | 4 stars and 20 ratings, which cut the $0.01 junk |
| `rating` | Avg. customer review | highest `weightedRating` first | 20 ratings |

`weightedRating` weighs stars by how many people gave them, so 4.7 stars from 90,000 ratings
ranks above 4.8 stars from 12. Every product starts as if 100 people had rated it 3 stars
(the lowest stars a rated product can have and still be returned), and its real ratings are
added to those:
(n x stars + 100 x 3) / (n + 100), where n is the rating count.

| Stars | Ratings | `weightedRating` |
|---|---|---|
| 5.0 | 47 | 3.639 |
| 4.9 | 200 | 4.267 |
| 4.9 | 3,627 | 4.849 |
| 4.8 | 35,896 | 4.795 |

A product's score depends only on its own stars and count, so it is the same in every
answer. A product needs several hundred ratings before its score is close to its stars:
that is the point, and it also means a well-liked product with few ratings ranks low. The
same score breaks ties in the `price` sort, so at one price the product with many ratings
comes first.

**What every search does:**

- Drops sponsored results and says how many (`sponsoredRemoved`).
- Merges pages, keeping each product once. `amazonRank` is its place in Amazon's own order.
- Checks price and stars itself as well as asking Amazon to, because Amazon applies its
  price filter loosely. `filteredOut` counts what that dropped.
- Returns Amazon's own filters for the search in `availableFilters` (brand, size and so
  on). Pass a code back in `filters` on the next call. Amazon takes one code per group: a
  search for "Anker or Belkin" is two searches.
- Returns what it read when a later page fails, with a warning naming the page and why.

Every sortable value is a plain number, so any caller can re-sort the results it already
has without asking Amazon again. In PowerShell:

```powershell
$r.results | Sort-Object price
$r.results | Sort-Object weightedRating -Descending
```

### `amazon_get_product`

`product` is an ASIN (`B088NRLMPV`) or any `www.amazon.com` product link.

It returns: prices, list price, savings, deal and coupon; availability, seller and delivery;
stars, the exact rating count and the star breakdown; Amazon's AI "Customers say" summary,
with each aspect's positive and negative counts and short review quotes; feature bullets,
description (cut at 4,000 characters), overview and details; Best Sellers Rank and
categories; the selected variation; image links; and the return policy. The main image also
comes back as an image, so the AI can see the product.

**Individual reviews are not included.** Signed out, Amazon shows "Sign in to see customer
reviews". The "Customers say" summary and its quotes are what a signed-out visitor gets.

A value that can't be read exactly is `null`, never a guess. `fetchedAt` says when the page
was read; prices change by the hour.

## How it reaches Amazon

- **Plain HTTPS, no browser** (`AmazonClient.cs`). Amazon has no data calls for search or
  product details (#28), so the service reads the pages a person would see and parses the
  HTML with AngleSharp.
- **Where each fact sits in the page** is in `AmazonSearchPage.cs` and `AmazonProductPage.cs`,
  and nowhere else.
- **Every request goes through one gate** (`HtmlSites/SiteGate.cs`): one in flight for the
  whole host, 3 to 5 seconds after another call's page, 2 to 3 seconds between pages of one
  search, 1 second around a picture, 30 seconds at most in the queue, and the hourly and
  daily caps. A whole call stops after 50 seconds.
- **robots.txt** (`HtmlSites/RobotsRules.cs`) is read at most once a day per host and checked
  before every request, for the names `Multiplex-Amazon` and `Multiplex`.
- **Parsed pages are kept for 10 minutes** in memory, so asking again, or changing our own
  minimums over pages already read, costs Amazon nothing. Another `sort`, or another of
  Amazon's filters, asks Amazon for different pages.
- **Pictures** come only from Amazon's image hosts, over HTTPS, without cookies.

## When Amazon says no

`AmazonPageClassifier.cs` sorts every answer before anything is read from it.

| Amazon's answer | What happens |
|---|---|
| A robot check a person can answer ("click to continue shopping", or "type the characters") | A person is asked, through a link (below) |
| Its "automated access" refusal, a sign-in page, an unexpected redirect, a check only a browser can pass | The service pauses |
| `429` with no wait named | The service pauses |
| `429` or a server error with a wait of 60 seconds or less | One retry, then an error |
| `404` | "Amazon has no product with the ASIN ..." |
| A page without the parts the service reads | "Amazon's page layout has changed: ... was not found" |

A pause stops every Amazon request for 15 minutes; each further block doubles it, up to 2
hours, and a success resets it. While paused, calls fail at once and say until when.

## When Amazon asks for a person

Nothing in the service answers a robot check. A person does, and the service only passes on
what they press or type (`AmazonClient.HandOver.cs`, `AmazonRobotCheckPage.cs`,
`../../HtmlSites/SiteHandOver.cs`).

1. Amazon answers a request with a check. The tool fails at once with a message for the AI
   to pass on: "Amazon wants a person to prove they're human ... Open
   `https://<this server>/check/amazon/<id>` ...". Every call after it fails the same way,
   with the same link, and asks Amazon nothing. Answers already cached are still served.
   Until the check is answered, the only requests to Amazon are the person's own answer,
   for the "type the characters" check one fetch of its picture, and a refresh of
   robots.txt if the copy held is over a day old.
2. The person opens the link. It is a Multiplex page, so it needs Multiplex sign-in. For the
   "continue shopping" check it shows one button. For the "type the characters" check it
   shows Amazon's picture, fetched by the server, and a box to type in.
3. The server sends that answer to Amazon on Amazon's own form, from the session the check
   was shown to, with the service's honest user agent.
4. When Amazon accepts it, requests are open again, and they carry the cookies that pass
   left behind, to `www.amazon.com` only.

**Why the link is not to Amazon itself.** Amazon records a passed check in the cookies of
the session that passed it. Answering on amazon.com in your own browser would clear your
browser, not this server.

**Limits:**

- The link works for 10 minutes. An unanswered check becomes a pause.
- Three answers Amazon does not accept close the check into a pause. So does a refused
  answer whose new check page the service can't read.
- Every pause ends the session a passed check had opened: afterwards the service is a new
  visitor again, with no cookies.
- Another check within 15 minutes of a passed one means the pass did not hold: the service
  pauses and offers no new link.
- Every address on the check page is checked first. The answer only ever goes to
  `https://www.amazon.com/errors/validateCaptcha` or `/errors_page/validateCaptcha`, as a
  GET, and the picture only comes from Amazon's image hosts. Anything else is not handed
  over; the service pauses.
- A check that needs Amazon's script to run in a browser can't be answered this way. The
  service pauses. A browser for that case would need the owner's approval (#96).

**This is a way past Amazon's robot check**, chosen by the owner (#121): a person answers,
and automated requests then carry on in that session.

What has been seen: on 2026-11-23 Amazon showed this service the "continue shopping" check
once, and the request after it went through unanswered. The "type the characters" check has
not been seen; its fixture is made by hand. The first real hand-over is the real test:
record on #121 whether the pass held for the requests after it.

## Files

| File | What it holds |
|---|---|
| `AmazonTools.cs` | The two tools |
| `AmazonClient.cs` | The request pipeline: robots.txt, the gate, the pause, the one retry, the cache, the picture |
| `AmazonSearch.cs` | Input rules, the request address, merging pages, our filters, the three sorts |
| `AmazonSearchPage.cs`, `AmazonProductPage.cs` | Everything the service knows about Amazon's markup |
| `AmazonPageClassifier.cs` | What an answer is |
| `AmazonClient.HandOver.cs`, `AmazonRobotCheckPage.cs` | The open robot check, its session's cookies, reading Amazon's check form, sending a person's answer |
| `AmazonText.cs`, `AmazonModels.cs` | Reading numbers out of text; the answer shapes |
| `../../HtmlSites/` | The gate, robots.txt rules, the pause, the errors and the hand-over pages, which know nothing about Amazon |

## Tests

They are in `tests/Multiplex.Tests/Services/Amazon/` and `tests/Multiplex.Tests/HtmlSites/`.

| Tests | Needs | What they cover |
|---|---|---|
| Page parsing | nothing | Each field, against trimmed copies of real pages in `Fixtures/`; a changed layout is a named error |
| Search logic | nothing | Input rules, the request address, merging, filters, the sorts (with numbers worked by hand) |
| Classifier, robots.txt, gate | nothing | Every kind of answer; robots.txt rules; spacing, queue, caps and deadline on a fake clock |
| Client | nothing | Against a fake Amazon: the honest identity, blocks and the pause, retries, redirects, the cache, the picture, the log |
| Hand-over | nothing | The link, the wait, the answer in the check's own session, a pass, wrong answers, expiry, and the page itself on the real host (sign-in, the antiforgery token) |
| Through MCP | nothing | The real host with a fake Amazon behind it: the tool list, both tools, tool errors |
| Recorded pages | pages in `Recorded/` | Whole real pages parse, and trim to the same facts |
| Live | `MULTIPLEX_LIVE_TESTS=1` | The real amazon.com, at most eight requests a run, checking invariants |

```bash
dotnet test --filter "FullyQualifiedName~Amazon|FullyQualifiedName~HtmlSites"
```

**Fixtures.** The repository is public, so a committed fixture is a trimmed page: only the
parts the page classes read, Amazon's prose replaced with placeholders, no scripts and no
visitor ids. A test fails if a fixture breaks that. To refresh them:

1. Save whole pages as `search-*.html` or `product-*.html` in
   `tests/Multiplex.Tests/Services/Amazon/Recorded/` (gitignored). A failing live run saves
   the pages it fetched there.
2. Run the tests. `AmazonRecordedPagesTests` checks each page and writes a trimmed copy to
   `Recorded/trimmed/`.
3. Copy the trimmed copies into `Fixtures/`.

`product-unavailable.html`, `product-no-ratings.html` and three of the `blocked-*.html`
files are made by hand; each says so in a comment. `blocked-continue-shopping.html` is the
real check page from 2026-11-23, with its token replaced.

When Amazon changes its markup, the live test fails first. Update the page class, then
refresh the fixtures.

## Icon

`wwwroot/icons/amazon.png` (256x256, at most 10 KB), Amazon's "a" and smile mark from Amazon's own image host, `https://m.media-amazon.com/images/G/01/anywhere/a_smile_196x196.png` (amazon.com's home page declares no icon to a plain HTTP client; its `/favicon.ico` is the same mark at 48x48). It is scaled up from 196x196 to 256x256, at most 10 KB, the one file ChatGPT's "Add custom MCP server" form takes as an app's icon (#138). Clients that read `serverInfo` show it beside the service, with its title and website (#135). ChatGPT shows the icon uploaded when its app is added, so upload this file in that form; it cannot be changed on an existing app. To refresh it, fetch the same URL again and fit it the same way (Phase 1 of the `web-api-discovery` skill).
