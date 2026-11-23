# Inventory Aggregation Service

A small .NET service that reads every record from the paginated Inventory API, checks that what it
collected is complete and consistent, aggregates it by category, and serves the result from one endpoint:

```
GET /api/inventory/summary
```

Contents: [Run it](#run-it) · [The endpoint](#the-endpoint) · [Architecture](#architecture) ·
[How the dataset is verified](#how-the-dataset-is-verified) · [When the upstream misbehaves](#when-the-upstream-misbehaves) ·
[Decisions I made](#decisions-i-made-where-the-brief-left-room) · [Known limitations](#known-limitations-and-what-i-would-do-next)

## Run it

Prerequisite: the .NET SDK, version 8 or newer (`dotnet --version`).

**1. Configure the upstream API.** The base URL and the API key are not in the repository. Set them with
user secrets:

```bash
dotnet user-secrets set "InventoryApi:BaseUrl" "<base URL from the assignment email>" --project src/InventoryAggregation.Api
dotnet user-secrets set "InventoryApi:ApiKey"  "<your API key>"                        --project src/InventoryAggregation.Api
```

or with environment variables (note the double underscore):

```bash
# bash
export InventoryApi__BaseUrl="<base URL>"
export InventoryApi__ApiKey="<your API key>"
```

```powershell
# PowerShell
$env:InventoryApi__BaseUrl = "<base URL>"
$env:InventoryApi__ApiKey  = "<your API key>"
```

The key is sent in the `X-Api-Key` request header. If it needs to go in a different header, set
`InventoryApi:ApiKeyHeaderName` the same way. The app refuses to start if the URL or the key is missing,
and the error says which one.

**2. Start the service.**

```bash
dotnet run --project src/InventoryAggregation.Api
```

It listens on `http://localhost:5080`. (`dotnet run` uses the Development environment, which is the one
that reads user secrets.)

**3. Call the endpoint.**

```bash
curl http://localhost:5080/api/inventory/summary
```

**4. Run the tests.**

```bash
dotnet test
```

99 tests, no network access needed, about a second to run.

### Settings

All under `InventoryApi` in [appsettings.json](src/InventoryAggregation.Api/appsettings.json), each
overridable by environment variable.

| Setting | Default | Meaning |
|---|---|---|
| `PageSize` | 25 | Records per upstream request (the API allows 1-25) |
| `MaxAttemptsPerPage` | 4 | Tries for one page, including the first |
| `AttemptTimeout` | 10 s | Time limit for a single upstream request |
| `BaseRetryDelay` / `MaxRetryDelay` | 0.5 s / 5 s | Backoff before the first retry, and its cap |
| `MaxRetryAfter` | 10 s | Longest `Retry-After` the service will wait out |
| `TotalTimeout` | 60 s | Time limit for collecting the whole dataset |

## The endpoint

`GET /api/inventory/summary` returns `200 OK`:

```json
{
  "categories": [
    {
      "category": "Electronics",
      "totalInventoryValue": 1799.88,
      "availableRecordCount": 1,
      "mostRecentlyUpdated": {
        "id": 1001,
        "name": "Thermal Receipt Printer",
        "lastUpdated": "2026-10-15T12:30:00Z"
      },
      "items": [
        {
          "id": 1001,
          "name": "Thermal Receipt Printer",
          "price": 149.99,
          "quantity": 12,
          "lastUpdated": "2026-10-15T12:30:00Z"
        }
      ]
    }
  ],
  "recordCounts": { "retrieved": 157, "available": 141, "excluded": 16 }
}
```

(The values above are illustrative.)

This is the shape in the brief with one addition, **`recordCounts`**. It lets a caller check the response
against the upstream themselves: `retrieved` is the upstream's `totalRecords`, `retrieved = available + excluded`,
and `available` equals the sum of every category's `availableRecordCount`. The brief asks for evidence
that the dataset is complete, and this puts that evidence in the response instead of only in my logs.

When the dataset cannot be obtained, the response is an RFC 9457 problem document, never a partial result
and never a stack trace:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.3",
  "title": "The inventory API is unavailable.",
  "status": 502,
  "detail": "Page 3 failed after 4 attempt(s): the API responded 503.",
  "failureKind": "Unavailable"
}
```

`failureKind` is one of `Unavailable`, `TimedOut`, `RateLimited`, `Rejected`, `InvalidData`. The table
under [When the upstream misbehaves](#when-the-upstream-misbehaves) says which status each one maps to.

## Architecture

```
GET /api/inventory/summary
 └─ InventorySummaryEndpoint          our HTTP contract: result -> 200, failure -> 502 / 503 / 504
     ├─ IInventorySource              "every record, or the reason why not"   <- the seam
     │   └─ PaginatedInventorySource  walks the pages, proves the set is complete
     │       └─ IInventoryPageClient
     │           └─ InventoryPageClient      the only class that speaks HTTP: retries, timeouts
     │               └─ InventoryPageValidator   "is this 200 really the page I asked for?"
     └─ InventoryAggregator           pure function: records -> category summaries
```

There are three things in this service that change for different reasons, and the code is split along
those lines:

- **The business rules** ([Inventory/](src/InventoryAggregation.Api/Inventory/)). `InventoryAggregator`
  is a pure, synchronous function over `InventoryRecord`s. It does not know the data came over HTTP, in
  pages, or with retries. Its tests build records in memory and nothing else. A new business rule is a
  change to this one class.
- **The upstream API and its bad habits** ([Upstream/](src/InventoryAggregation.Api/Upstream/)). Split
  in two because there are two different questions. `InventoryPageClient` answers "can I get one
  trustworthy page?": status codes, transport errors, timeouts, retries, and validating the body.
  `PaginatedInventorySource` answers "do the pages add up to the whole dataset?": totals that agree,
  no record delivered twice, count matches. Keeping them apart means the pagination logic is tested
  with a fake page client and no HTTP at all, and the HTTP logic is tested one page at a time.
- **Our own HTTP surface** ([Endpoints/](src/InventoryAggregation.Api/Endpoints/)). Translating a result
  into a status code is the endpoint's job and nobody else's.

`IInventorySource` is the seam between the first two. It returns a `FetchResult`: either the records or a
`FetchFailure` with a kind and a caller-safe message. I modelled upstream failure as a return value
because for this service it is an expected outcome, not an exceptional one. Exceptions are left for bugs,
and a global handler turns those into a plain 500 problem response.

Two interfaces exist (`IInventorySource`, `IInventoryPageClient`) because those are the two places where
tests need to substitute something. The aggregator is registered in DI as a concrete class: it is pure,
so there is nothing to fake.

`HttpClient` comes from `IHttpClientFactory` as a typed client. The source that uses it is registered as
transient rather than singleton, so the typed client is never captured for the life of the process and
the factory can keep recycling the underlying handler.

## How the dataset is verified

The brief says a 200 is not a guarantee. These checks are in the code, each with a test.

**On every page** ([InventoryPageValidator.cs](src/InventoryAggregation.Api/Upstream/InventoryPageValidator.cs)).
A page that fails any of these is treated as a bad response and requested again:

- The body is valid JSON with a `records` array and all four paging fields present.
- `page` and `pageSize` echo what was requested.
- `totalPages` equals `ceil(totalRecords / pageSize)`.
- The page holds exactly the number of records its position implies: full pages everywhere except the
  last, which holds the remainder. This is the check that catches a truncated page.
- `totalRecords` matches what the first page reported. The dataset is documented as static, so a total
  that changes mid-run is a fault, not an update.
- Every record has all six fields, a non-blank category, a non-negative price and a parseable
  timestamp, and no id appears twice on the page.

The wire types are fully nullable on purpose. `System.Text.Json` fills a missing `int` with `0`, so a
record that arrived without `quantity` would have been read as quantity 0 and then silently excluded as
out of stock. Nullable fields keep "missing" and "zero" apart until the validator has looked.

**Across pages** ([PaginatedInventorySource.cs](src/InventoryAggregation.Api/Upstream/PaginatedInventorySource.cs)):

- No record id appears on two pages.
- The number of records collected equals `totalRecords`.

If either fails, the request fails. Nothing is aggregated from a set that could not be shown to be whole.

## When the upstream misbehaves

| Upstream behaviour | Retried? | If it persists, my caller gets |
|---|---|---|
| 500, 502, 503, 504, any other 5xx, 408 | Yes | `502` `Unavailable` |
| 429 | Yes, waiting at least `Retry-After` | `503` `RateLimited`, with `Retry-After` passed on |
| Connection refused, DNS failure, TLS failure, connection dropped mid-body | Yes | `502` `Unavailable` |
| No complete response within `AttemptTimeout` | Yes | `504` `TimedOut` |
| 200 with malformed JSON, or with a page that fails validation | Yes | `502` `InvalidData` |
| 400, 401, 403, 404, any other 4xx | No | `502` `Rejected` |
| Pages that are each valid but disagree with each other | No | `502` `InvalidData` |
| The whole collection runs past `TotalTimeout` | n/a | `504` `TimedOut` |

The rule behind the first column: a status the brief does not list is still classified, by what kind of
statement it makes. A 5xx or a 408 describes the server's condition at that moment, so the same request
may work later and is retried. A 4xx describes my request, so sending it again unchanged would only get
the same answer and add load.

**Retry policy.** Up to 4 attempts per page. The wait doubles each time (0.5 s, 1 s, 2 s, capped at 5 s)
and is jittered to between 50% and 100% of that, so that callers who failed together do not retry
together. A `Retry-After` header is honoured as a minimum wait, as seconds or as a date. If the server
asks for more than `MaxRetryAfter` (10 s), the service stops immediately and passes the `Retry-After` on
to its own caller instead of holding the request open. Each attempt has its own 10 s time limit covering
headers and body, and the whole collection has a 60 s deadline, so the worst case for a caller is a
known number.

**Why the retry loop is hand-written.** I did not use Polly or `Microsoft.Extensions.Http.Resilience`.
The decision to retry here depends on the parsed body, because a 200 can carry a bad page, and a
resilience handler in the `HttpClient` pipeline only sees status codes and exceptions. I would have
needed a second retry mechanism above it anyway. One explicit loop of about forty lines
([InventoryPageClient.cs](src/InventoryAggregation.Api/Upstream/InventoryPageClient.cs)) keeps the
attempt, the classification and the backoff in one place that can be stepped through. The backoff
calculation is a pure function with exact tests. With several upstreams, or a need for circuit breaking
or hedging, I would switch to the library.

**Cancellation.** If the caller disconnects, the in-flight upstream request is cancelled and no retry
is made. That is kept distinct from a timeout: only the service's own time limits are reported as
`TimedOut`.

## Decisions I made where the brief left room

1. **"Quantity of zero" is applied as "quantity of zero or less".** The brief's reason for the rule is
   that such records "are not available stock". A negative quantity is not available stock either.
2. **Every figure comes from available records only**, including `mostRecentlyUpdated`. An out-of-stock
   record never appears as a category's most recent, even if it is the newest.
3. **A category with no available records is omitted**, which follows from filtering before grouping.
4. **Categories are matched ignoring case and surrounding whitespace.** `Electronics`, `electronics`
   and `" Electronics "` are one category. The name reported is the spelling on its lowest-id record.
5. **Ties on `lastUpdated` go to the lowest id**, so the answer never depends on the order records arrived.
6. **Money is `decimal` and is not rounded.** Sums are exact. Precision is passed through as the upstream
   sent it, so a price of `4.25` and a quantity of `4` gives `17.00`.
7. **Output order is fixed**: categories by name, items by id.
8. **Timestamps are compared as instants** and written in UTC with a `Z` suffix. An upstream timestamp
   with no offset is read as UTC, as the API documents, not as the local time of the machine running this.
9. **The service fails closed.** If the complete dataset cannot be obtained and verified, the answer is
   an error. Figures computed from part of the data would look exactly as trustworthy as correct ones.
10. **A record that violates the documented contract fails the page** (a missing field, a blank category,
    a negative price). I do not guess a value or drop the record quietly, since either would change a
    money total without anyone knowing.
11. **A record id seen on two pages is treated as an error, not de-duplicated.** With offset pagination,
    a record appearing twice usually means another record was pushed out and is missing. De-duplicating
    would hide that.
12. **Pages are fetched one at a time at the maximum page size (25).** That is seven requests for the
    157 records in the brief's example. Fetching them in parallel would save little and would multiply
    load on the upstream exactly when it is already failing.
13. **Failures use gateway status codes** (502, 503, 504), because that is what this service is to its
    callers. The `detail` text is written to be safe to show: no key, no upstream host, no stack trace.

## Known limitations and what I would do next

- **I have not yet run this against the live Inventory API.** Everything above is tested against a
  stubbed upstream that reproduces the documented contract and the failure modes I could anticipate. I
  do not yet know which faults the real API actually produces, and that matters for the next point.
- **Failing closed has a cost if bad data is permanent rather than intermittent.** If the real dataset
  permanently contains a malformed record or a duplicated id, this endpoint will return 502 every time,
  with a message naming the record and the rule. That is deliberate, but the right production answer
  might be to quarantine and report such records instead, and I would want to see the real data and
  talk to its owner before choosing.
- **A bad first page poisons the run.** The first page's `totalRecords` is taken as the reference. If
  that one response is wrong, every later page looks inconsistent and the request fails. The next
  request starts fresh. A second pass before giving up would fix this.
- **No caching.** Every call re-reads the whole upstream, one request per page. Since the dataset is
  static, I would cache the verified snapshot, share one in-flight fetch between concurrent callers,
  and consider serving the last good snapshot, marked as stale, while the upstream is down.
- **No circuit breaker.** During an upstream outage every caller still pays for the full retry sequence
  (about 10 s against a closed port in a local test) before getting its 502.
- **The response is unbounded.** It contains every available record, as the brief requires. A much
  larger dataset would need paging or filtering on this endpoint too.
- **Observability is logs only.** Each retry and each rejection is logged with the page, the attempt and
  the reason, but there are no metrics, traces or health checks.
- **The endpoint itself is open.** No authentication, rate limiting or OpenAPI description.
- **.NET 8 is past end of support.** The brief names .NET 8 as the minimum and that is what I built and
  tested on, but it left support on 10 November 2026. The projects set `RollForward=Major`, so they
  start on a .NET 10 or 11 runtime if 8 is not installed; I have not tested that. Retargeting to .NET 10
  should be the target framework plus one test package version.
- **Tests stop at the network boundary.** The end-to-end tests run the real pipeline against a stub.
  Nothing in the test suite proves compatibility with the real API beyond its documented example. One
  endpoint test (the 504 case) uses a real 50 ms timer; all other timing tests use a fake clock.
