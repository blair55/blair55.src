---
title: "Parallelising Heterogeneous Async Calls"
date: 2025-12-04T01:00:00Z
draft: false
tags:
  - fsharp
  - parallelism
  - concurrency
  - async
  - task
---

This is a post for the [FSharp Advent Calendar 2025](https://sergeytihon.com/2025/11/03/f-advent-calendar-in-english-2025/).

A particular feature of F# 10 got me thinking about approaches to parallelism, past and present.

### Async.Parallel

This system library function is built for orchestrating async calls with _homogenous_ return types. It has an overload that permits controlling the degree of parallelism.

```fsharp
static member Parallel:
   computations: seq<Async<'T>> -> Async<'T array>
```

It's great for situations where we want to make multiple calls to the same api endpoint with different arguments - because each call expects the same response, we end up with a collection of the response type when all the calls are complete. And given the calls are made in parallel, we might only have to wait as long as the longest call.

```fsharp
let fetchData i: Async<string> = ...

let it: string array =
  [ 1..10 ]
  |> List.map fetchData
  |> Async.Parallel
  |> Async.RunSynchronously
```

### Hetorogenous Concurrency

But what about when we want to make multiple calls to different endpoints? i.e. We're expecting different looking data. Let's say we have to load some data for a dashboard when a user logs in. We have three calls to make that return _heterogeneous_ data:

```fsharp
let getUserDetails userId: Async<UserDetails> = async { ... }

let getNotifications userId: Async<Notice List> = async { ... }

let getActivePromotion (): Async<Promotion Option> = async { ... }

type DashboardData = UserDetails * Notice List * Promotion Option
```

We could _**unify**_ the return types in order to leverage `Async.Parallel`. There's no good reason to use this approach anymore, though I have used it in the past!

```fsharp
// Unifying type
type DashboardAsync =
  | GetUserDetails of UserDetails
  | GetNotifications of Notice List
  | GetActivePromotion of Promotion Option

let getDashboardData userId : Async<DashboardData> =

  // construct a list of homogeneous async calls from heterogeneous ones
  [ getUserDetails userId |> Async.map GetUserDetails
    getNotifications userId |> Async.map GetNotifications
    getActivePromotion () |> Async.map GetActivePromotion
  ]
  |> Async.Parallel
  |> Async.map (fun (results: DashboardAsync array) ->

      // start with empty placeholders then fold over the results to collect
      ((None, None, None), results)
      ||> Array.fold (fun (d, n, p) ->
        function
        | GetUserDetails details -> Some details, n, p
        | GetNotifications notices -> d, Some notices, p
        | GetActivePromotion promo -> d, n, Some promo) 

      // verify expected data
      |> function
        | Some details, Some notifications, Some promotion ->
            details, notifications, promotion
        | _ -> failwith "Missing data"
  )

module Async =
  let map f x = async {
    let! y = x
    return f y
  }
```

This approach achieves making concurrent calls, but we can't ignore the downsides:

* it requires an extra type and a helper function
* it requires a fold function costing mental overhead
* it requires extra logic to check all the calls have completed
* it's fiddly to add or remove calls
* compiler support is lost / it might throw an exception!

To illustrate the point about compiler support: the constructed list of `DashboardAsync`s could become out-of-sync with the verification logic: we could, for example, accidentally omit the `getNotifications` call.

```fsharp
  [ getUserDetails userId |> Async.map GetUserDetails
    // getNotifications userId |> Async.map GetNotifications
    getActivePromotion () |> Async.map GetActivePromotion
  ]
```

The code would compile happily but throw an exception at runtime - very un-F# like! We could mitigate by using the `Result` type to model the verification of the outcome (and also the async calls i.e. `AsyncResult`) but it would add weight to the solution.

### Async.StartChild

The obvious improvement here is to kick off the calls with `Async.StartChild`. Parallelism achieved with none of the downsides from the previous approach. However, StartChild function returns an `Async<Async<'T>>`, so we have to use another `let!` to get to the result.

```fsharp
let getDashboardData userId : Async<DashboardData> =
  async {
    let! detailsAsync = getUserDetails userId |> Async.StartChild
    let! notificationsAsync = getNotifications userId |> Async.StartChild
    let! promotionAsync = getActivePromotion () |> Async.StartChild

    let! details = detailsAsync
    let! notifications = notificationsAsync
    let! promotion = promotionAsync
    return details, notifications, promotion
  }
```

### F# 10

The introduction of [support for `and!` in `task` expressions](https://learn.microsoft.com/en-us/dotnet/fsharp/whats-new/fsharp-10#support-for-and-in-task-expressions) in F# 10 brings the solution in its final form: Concurrent calls with almost no overhead. Map the result back to Async with `Async.AwaitTask` if required.

```fsharp
 // tasks awaited concurrently with and! in F# 10
let getDashboardData userId : Task<DashboardData> = task {
  let! details = getUserDetails userId |> Async.StartAsTask
  and! notifications = getNotifications userId |> Async.StartAsTask
  and! promotion = getActivePromotion () |> Async.StartAsTask
  return details, notifications, promotion
}
```