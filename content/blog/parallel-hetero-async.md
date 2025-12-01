---
title: "Parallelising Heterogeneous Async Calls"
date: 2025-12-01T10:00:00Z
draft: true
tags:
  - fsharp
  - functional
  - parallelism
  - concurrency
  - async
  - task
---

## Sub title: What's the common name for this pattern?

> This is a post for the [FSharp Advent Calendar 2025](https://sergeytihon.com/2025/11/03/f-advent-calendar-in-english-2025/)

The [support for `and!` in task expressions](https://learn.microsoft.com/en-us/dotnet/fsharp/whats-new/fsharp-10#support-for-and-in-task-expressions) in F# 10 made me think about how I approach this with F# `Async`.

### `Async.Parallel`

This library function is built for orchestrating async calls with _homogenous_ return types. It has an overload that permits controlling the degree of parallelism.

```fsharp
static member Parallel:
   computations: seq<Async<'T>> -> Async<'T array>
```

It's great for situations where we want to make multiple calls to the same api endpoint with different arguments - because each call expects the same response, we end up with a collection of the response type.

```fsharp
let fetchData i: Async<string> = ...

let it: string array =
  [ 1..10 ]
  |> List.map fetchData
  |> Async.Parallel
  |> Async.RunSynchronously
```

### Hetorogenous data

But what about when I want to make multiple calls to different endpoints? I.e. I'm expecting different looking data. Let's say we have to load some data for a dashboard when a user logs in. I have three calls to make that return heterogeneous data:

```fsharp
let getUserDetails userId: Async<UserDetails> = ...

let getNotifications userId: Async<Notice List> = ...

let getActivePromotion (): Async<Promotion Option> = ...
```

### F# 10

I can make these calls sequentially. Or I can use the `task` computation expression with `and!` in F# 10 to achieve parallelism:

```fsharp
type DashboardData(Details: UserDetails, Notifications: Notice List, Promotion: Promotion Option) = 
  member this.Details = Details
  member this.Notifications = Notifications
  member this.Promotion = Promotion

 // tasks awaited concurrently with and! in F# 10
let getDashboardData userId : Task<DashboardData> = task {
  let! details = getUserDetails userId |> Async.StartAsTask
  and! notifications = getNotifications userId |> Async.StartAsTask
  and! promotion = getActivePromotion () |> Async.StartAsTask
  return DashboardData(details, notifications, promotion)
}
```

### Another Way

But without F#10, and with only the `async` CE, how can we parallelise?

We can _unify_ the types in order to leverage `Async.Parallel`.

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
            DashboardData(details, notifications, promotion)
        | _ -> failwith "Missing data"
  )

module Async = // helper function
  let map f x = async {
    let! y = x
    return f y
  }
```

This approach achieves the aim of making calls concurrently. But we can't ignore the downsides over the F# 10 `and!` method:

* it requires an extra type and a helper function
* it requires a fold function costing mental overhead
* it requires extra logic to check all the calls have completed
* it's fiddly to add or remove async calls
* compiler support is lost / it might throw an exception!

To illustrate the point about compiler support: the constructed list of `DashboardAsync`s could get out-of-sync with the verification logic before the return: we could, for example, accidentally omit the `getNotifications` call.

```fsharp
  [ getUserDetails userId |> Async.map GetUserDetails
    // getNotifications userId |> Async.map GetNotifications
    getActivePromotion () |> Async.map GetActivePromotion
  ]
```

The code would compile happily but throw an exception at runtime - very un-F# like! We could mitigate by using the `Result` type to model the verification of the outcome, and also the async calls i.e. `AsyncResult`. Just depends what your stack can tolerate.

## So what do we call this?

Besides sharing this as a general pattern (despite its drawbacks), I'd love to know if this approach is already known to you, and if so what do you call it? I've used it for a few years without ever giving it an appropriate handle. Some suggestions:

- Unification for Parallelism
- Heterogeneous Concurrency
- Compromised Promises(!)

Please let me know at [@nickblair.dev](https://bsky.app/profile/nickblair.dev)
