# Inventory Aggregation Service

A small .NET service that reads every record from the paginated Inventory API, checks that what it
collected is complete and consistent, aggregates it by category, and serves the result from one endpoint:

```
GET /api/inventory/summary
```

Contents: [Run it](#run-it) · [The endpoint](#the-endpoint) · [What the real API does](#what-the-real-api-does) ·
[Architecture](#architecture) · [How the dataset is verified](#how-the-dataset-is-verified) ·
[When the upstream misbehaves](#when-the-upstream-misbehaves) · [Decisions I made](#decisions-i-made-where-the-brief-left-room) ·
[Known limitations](#known-limitations-and-what-i-would-do-next)

## Run it

Prerequisite: the .NET 10 SDK (`dotnet --version` should print `10.x`).

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

The key is sent in the `X-Api-Key` request header. The app refuses to start if the URL or the key is
missing, and the error says which one.

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

Expect this to take **about 13 seconds**. That is not the service being slow: the upstream allows 5
requests per 10 seconds and a complete read needs 7, so every read has to wait out one rate-limit
window. The console log shows the 429 and the wait. See [What the real API does](#what-the-real-api-does).

**4. Run the tests.**

```bash
dotnet test
```

111 tests, no network access needed, a few seconds to run.

### Settings

All under `InventoryApi` in [appsettings.json](src/InventoryAggregation.Api/appsettings.json), each
overridable by environment variable.

| Setting | Default | Meaning |
|---|---|---|
| `ApiKeyHeaderName` | `X-Api-Key` | The request header the key is sent in |
| `PageSize` | 25 | Records per upstream request (the API allows 1-25) |
| `MaxAttemptsPerPage` | 4 | Tries for one page, including the first |
| `AttemptTimeout` | 10 s | Time limit for a single upstream request |
| `BaseRetryDelay` / `MaxRetryDelay` | 0.5 s / 5 s | Backoff before the first retry, and its cap |
| `MaxRetryAfter` | 30 s | Longest `Retry-After` the service will wait out |
| `TotalTimeout` | 60 s | Time limit for collecting the whole dataset |

## The endpoint

`GET /api/inventory/summary` returns `200 OK`. This is real output from the live API, abridged to the
first category and its first item:

```json
{
  "categories": [
    {
      "category": "Electronics",
      "totalInventoryValue": 18253.18,
      "availableRecordCount": 25,
      "mostRecentlyUpdated": {
        "id": 1037,
        "name": "Barcode Scanner MK-1037",
        "lastUpdated": "2026-09-23T17:05:00Z"
      },
      "items": [
        {
          "id": 1016,
          "name": "Label Printer MK-1016",
          "price": 53.74,
          "quantity": 14,
          "lastUpdated": "2026-09-20T01:10:00Z"
        }
      ]
    }
  ],
  "recordCounts": { "retrieved": 157, "available": 136, "excluded": 21 }
}
```

The full result against the live API on 23 November 2026, for comparison with yours:

| Category | Available records | Total inventory value | Most recently updated |
|---|---|---|---|
| Electronics | 25 | 18253.18 | 1037 Barcode Scanner MK-1037, 2026-09-23T17:05:00Z |
| Home & Kitchen | 27 | 17151.51 | 1019 Electric Kettle MK-1019, 2026-09-23T20:36:00Z |
| Office Supplies | 30 | 15996.70 | 1091 Desk Organizer MK-1091, 2026-09-23T06:32:00Z |
| Outdoor | 27 | 17822.93 | 1073 Insulated Cooler MK-1073, 2026-09-23T10:03:00Z |
| Toys | 27 | 15879.52 | 1055 Marble Run MK-1055, 2026-09-23T13:34:00Z |

136 available records and 21 excluded make up the API's 157. I checked these figures against a separate
calculation done directly on the raw API responses, outside this codebase, and they agree.

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

## What the real API does

Before settling the validation rules I probed the API directly with `curl`: every page at page sizes 25,
20 and 10, repeated passes, out-of-range parameters, a missing and a wrong key, and a burst of parallel
requests. This is what it does that the brief does not say, as observed on 23 November 2026.

| Observed behaviour | What the service does about it |
|---|---|
| **Rate limiting.** More than 5 requests in 10 seconds gets `429` with `Retry-After: 10` and the body "Rate limit of 5 requests per 10 seconds exceeded." The status is not in the brief. A full read is 7 requests at the largest page size, so every read hits it. | Treats 429 as temporary, waits the `Retry-After` plus a little jitter, and carries on from the same page. |
| **Overlapping pages.** Every even-numbered page starts with a repeat of the previous page's last record, so it holds `pageSize + 1` rows. At page size 25 that is 160 rows for 157 records (ids 1025, 1075 and 1125 arrive twice). The pattern is the same at page sizes 20 and 10. | Keeps one copy of an identical repeat, so no record's stock is counted twice, and judges completeness on distinct ids rather than rows. |
| **Inconsistent category names.** 6 categories are spelled 24 ways, differing in case and in leading and trailing spaces: `Home & Kitchen`, `"  home & kitchen "`, `OFFICE SUPPLIES`, `outdoor`. | Groups them under one normalised name. See decision 4. |
| **A category with nothing in stock.** All 7 `Discontinued` records have quantity 0. | The category does not appear in the output. See decision 3. |
| **Undocumented `401`** for a missing or wrong key. | Not retried. Reported as `502` `Rejected`. |
| **A page past the end** returns `200` with an empty `records` array, not `400`. | Not relied on. The loop stops at `totalPages`. |

The per-record data itself is clean: all 157 records have every field, no negative numbers, two-decimal
prices, and UTC timestamps with a `Z`. 21 records have quantity 0. Responses are deterministic: the
same request returned the same bytes on every pass, and the service's output was byte-for-byte identical
across runs.

**What I did not see** in roughly 100 requests: a 5xx, a timeout, a truncated or malformed body, or two
different versions of the same record. The service handles all of those, but that handling is tested
against a stub only, not observed against the real API.

## Architecture

```
GET /api/inventory/summary
 └─ InventorySummaryEndpoint          our HTTP contract: result -> 200, failure -> 502 / 503 / 504
     ├─ IInventorySource              "every record, or the reason why not"   <- the seam
     │   └─ PaginatedInventorySource  walks the pages, removes repeats, proves the set is complete
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
  `PaginatedInventorySource` answers "do the pages add up to the whole dataset?": removing repeats and
  checking the distinct count against the total. Keeping them apart means the pagination logic is tested
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
- The page holds at least the number of records its position implies: a full page everywhere except
  the last, which holds the remainder. This is what catches a truncated page. It is a minimum and not an
  exact count because of the overlap described above.
- `totalRecords` matches what the first page reported. The dataset is documented as static, so a total
  that changes mid-run is a fault, not an update.
- Every record has all six fields, a non-blank category, a non-negative price and a parseable timestamp.

The wire types are fully nullable on purpose. `System.Text.Json` fills a missing `int` with `0`, so a
record that arrived without `quantity` would have been read as quantity 0 and then silently excluded as
out of stock. Nullable fields keep "missing" and "zero" apart until the validator has looked.

**Across pages** ([PaginatedInventorySource.cs](src/InventoryAggregation.Api/Upstream/PaginatedInventorySource.cs)):

- A record id that arrives a second time with identical contents is dropped, so it is counted once.
- A record id that arrives a second time with **different** contents fails the request. There is no
  basis for choosing between two versions, and either choice changes a money total.
- The number of **distinct** records must equal `totalRecords`. Counting rows would not prove anything,
  since rows include the repeats. This is also what catches a repeat that has pushed a real record out:
  the rows would still add up, the distinct records would not.

If any of these fail, the request fails. Nothing is aggregated from a set that could not be shown to be
whole. This is the service's own log for one read of the real API (category prefixes removed), showing
the oversized even pages, the rate limit, and the final check:

```
Page 1 of 7: 25 row(s)
Page 2 of 7: 26 row(s)
Page 3 of 7: 25 row(s)
Page 4 of 7: 26 row(s)
Page 5 of 7: 25 row(s)
Inventory API answered page 6 with 429: {"title":"Too Many Requests","status":429,"detail":"Rate limit of 5 requests per 10 seconds exceeded.", ...
Page 6 attempt 1 of 4 failed: the API is rate limiting us (429). Retrying in 9491 ms
Page 6 of 7: 26 row(s)
Page 7 of 7: 7 row(s)
Retrieved 157 distinct records from 7 page(s); dropped 3 repeated row(s)
```

## When the upstream misbehaves

| Upstream behaviour | Retried? | If it persists, my caller gets |
|---|---|---|
| 429 | Yes, after `Retry-After` plus jitter | `503` `RateLimited`, with `Retry-After` passed on |
| 500, 502, 503, 504, any other 5xx, 408 | Yes | `502` `Unavailable` |
| Connection refused, DNS failure, TLS failure, connection dropped mid-body | Yes | `502` `Unavailable` |
| No complete response within `AttemptTimeout` | Yes | `504` `TimedOut` |
| 200 with malformed JSON, or with a page that fails validation | Yes | `502` `InvalidData` |
| 400, 401, 403, 404, any other 4xx | No | `502` `Rejected` |
| Pages that are each valid but do not add up to the whole dataset | No | `502` `InvalidData` |
| The whole collection runs past `TotalTimeout` | n/a | `504` `TimedOut` |

The rule behind the first column: a status the brief does not list is still classified, by what kind of
statement it makes. A 5xx or a 408 describes the server's condition at that moment, so the same request
may work later and is retried. A 4xx describes my request, so sending it again unchanged would only get
the same answer and add load. 429 is the exception among the 4xx codes, because it explicitly says
"later".

**Retry policy.** Up to 4 attempts per page. The wait doubles each time (0.5 s, 1 s, 2 s, capped at 5 s)
and is jittered to between 50% and 100% of that, so that callers who failed together do not retry
together. When the server sends `Retry-After` (as seconds or as a date), that is waited out in full and
the backoff is added on top: the header is in whole seconds, so waiting exactly that long risks arriving
a fraction early and being refused again. If the server asks for more than `MaxRetryAfter` (30 s), the
service stops immediately and passes the `Retry-After` on to its own caller instead of holding the
request open. Each attempt has its own 10 s time limit covering headers and body, and the whole
collection has a 60 s deadline, so the worst case for a caller is a known number.

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

**Logging.** Every accepted page logs its row count. Every failed attempt logs the page, the attempt
number, the reason and the wait before the next try, and for a non-success status the start of the
upstream's own error body, which is usually the fastest route to a diagnosis. The framework's own
`HttpClient` request logging is turned down to warnings, because .NET 10 redacts the query string there
and the page number is the part worth seeing.

## Decisions I made where the brief left room

1. **"Quantity of zero" is applied as "quantity of zero or less".** The brief's reason for the rule is
   that such records "are not available stock". A negative quantity is not available stock either. (The
   real data has no negative quantities.)
2. **Every figure comes from available records only**, including `mostRecentlyUpdated`. An out-of-stock
   record never appears as a category's most recent, even if it is the newest.
3. **A category with no available records is omitted**, which follows from filtering before grouping.
   In the real data that removes `Discontinued` entirely. The alternative, listing it with a zero value
   and no most-recent record, seemed less faithful to "group the remaining records".
4. **Category names are normalised.** Leading and trailing whitespace is removed, inner runs of
   whitespace are collapsed, case is ignored for grouping, and the name is reported in Title Case. I
   chose Title Case because no existing spelling is canonical: the most common spelling is `Electronics`
   for one category, `OFFICE SUPPLIES` for another and `outdoor` for a third. Title Case is also the
   style of the brief's own example. The cost is that an acronym would be flattened (`TV` would become
   `Tv`).
5. **Ties on `lastUpdated` go to the lowest id**, so the answer never depends on the order records
   arrived. (The real data has no ties.)
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
11. **A repeated record is counted once, but only if the copies are identical.** The id is documented as
    unique, so an identical second copy is the same record delivered twice, and counting it twice would
    overstate stock. Two differing copies are an error.
12. **Rate limiting is handled by reacting to 429, not by pacing requests in advance.** I know the limit
    is 5 per 10 seconds only because the error message says so. It is not in the contract, and it could
    change. Obeying `Retry-After` keeps working if it does. The cost is one refused request per read.
13. **Pages are fetched one at a time at the maximum page size (25).** That is the fewest requests (7),
    and with a limit of 5 per 10 seconds there is nothing to gain from fetching them in parallel.
14. **Failures use gateway status codes** (502, 503, 504), because that is what this service is to its
    callers. The `detail` text is written to be safe to show: no key, no upstream host, no stack trace.

## Known limitations and what I would do next

- **Every call takes about 13 seconds.** A read needs 7 upstream requests and the upstream allows 5 per
  10 seconds, so each call waits out one rate-limit window. Nothing is cached. Since the dataset is
  static, the first thing I would add is a cache of the verified snapshot. I left it out because how
  stale a result may be is a decision for whoever consumes this, and it is a feature rather than part of
  getting the data right.
- **Concurrent callers compete for one upstream quota.** Two simultaneous calls need 14 upstream
  requests between them. In the one time I tried it, both returned the correct result, in 22 and 33
  seconds instead of 13. I have not tested more than two. With enough callers, some will use up their
  attempts on 429s and get a `503`, or run past the 60 s deadline and get a `504`. Sharing one in-flight
  read between concurrent callers would fix it, and the cache above would mostly hide it.
- **Some failure handling has only ever met a stub.** As listed under
  [What the real API does](#what-the-real-api-does), I did not see the real API produce a 5xx, a
  timeout, a malformed body or conflicting copies of a record. The tests cover them; real behaviour may
  differ in ways I have not anticipated.
- **Failing closed has a cost if bad data is permanent.** If the dataset ever permanently contains a
  malformed record, or two different versions of one id, this endpoint returns 502 every time, with a
  message naming the record and the rule. That is deliberate, but the right production answer might be
  to quarantine and report such records, and I would want to talk to the data's owner before choosing.
- **A bad first page poisons the run.** The first page's `totalRecords` is taken as the reference. If
  that one response is wrong, every later page looks inconsistent and the request fails. The next
  request starts fresh. A second pass before giving up would fix this.
- **Title Case is a guess at the canonical category names.** It is right for the six categories in the
  data. A real system should take canonical names from wherever categories are defined.
- **No circuit breaker.** During an upstream outage every caller still pays for the full retry sequence
  (about 10 s against a closed port in a local test) before getting its 502.
- **The response is unbounded.** It contains every available record, as the brief requires. A much
  larger dataset would need paging or filtering on this endpoint too.
- **Observability is logs only.** No metrics, traces or health checks.
- **The endpoint itself is open.** No authentication, rate limiting or OpenAPI description.
- **Tests stop at the network boundary.** The end-to-end tests run the real pipeline against a stub that
  reproduces the overlap and the 429 I observed. There is no automated test against the live API. Two
  endpoint tests use real time (a 50 ms timeout and a 1 s `Retry-After`); all other timing tests use a
  fake clock.
- **Built and tested on one machine**: Windows, .NET SDK 10.0.404. The projects set `RollForward=Major`,
  so they should start on a newer runtime if .NET 10 is not installed, but I have not tested that.
