---
title: "Combining monads"
date: 2019-08-02T11:36:05+01:00
draft: false
tags:
    - fsharp
    - functional
    - monads
---

The F# `Result<'a,'b>` type allows for concise control flow syntax. The `async { ... }` computation expression similarly minimizes the noise of asynchrony. Throw in the `Writer` monad for logging minus the intrinsic IO statements. How do you get the benefits of all three together? You need to combine...

> Source code [gist](https://gist.github.com/blair55/ed415eb479d639f471418f482545fa0e).

## Writer

The `Result` and `Async` types are core types in F# but `Writer` is not so we need a bit of boilerplate to get going. We define a single case union type in which the single case is parameterized with a function. The function expects a unit and returns a tuple. See [this post](http://codebetter.com/matthewpodwysocki/2010/02/02/a-kick-in-the-monads-writer-edition/) for details.

```fsharp
type Writer<'w, 't> =
  Writer of (unit -> 't * 'w)

module Writer =

  let run<'w,'t> (Writer w) : ('t * 'w) =
    w ()
```

## Bind

Now, lets cover the `bind` function for each type.

```fsharp
// bind signature
// ('a -> Wrapper<'b>) -> 'Wrapper<'a> -> Wrapper<'b>

module Result =

  let bind f = function
    | Ok x -> f x
    | Error e -> Error e

module Async =

  let bind f m = async {
    let! x = m
    return! f x }

module Writer =

  let bind f m =
    let (x, logs1) = run m
    let (y, logs2) = run (f x)
    Writer <| fun () -> y, logs1 @ logs2
```

A pattern is present in each of these functions. The signatures are obviously the same but the behaviours can also be generalized. If we were to describe what is going on in general terms we would say:

> Unwrap the outer type to reveal the inner value `x`. Then, run the given function `f` against the value and return the result re-wrapped in the outer type.

There is some subtlety in each to preserve the meaning of the wrapping type. In the `Result` case we only want to run the function if the unwrapped value is `Ok`. With `Async` there is no condition, but `return!` must be used to re-wrap the value. The `Writer` type requires us to unwrap twice: once to get the initial unwrapped value, then again to get the unwrapped value after applying the function. The logs from both are concatenated in a returned `Writer`.

## Combining

It seems sensible to order the types as follows:

```fsharp
Async < Writer < _, Result < _, _ > > >
```

The `Async` type always needs to be on the outside because typically we want to return this type to a framework, something else will be responsible for waiting for the async operation to complete. The middle type should be `Writer` because we always want to capture the output. If the `Result` and `Writer` positions were reversed we would only get the `Writer` type back if the `Result` type returned an `Ok` case, which is undesirable.


### The First Two

Let's combine just the middle two types and work our way up to three. I've found it useful to have the inner types combined as some workflows turn out not to be asynchronous and can easily be made so using `Async.map` to fit with other workflows.

```fsharp
module WriterResult =

  let bind f m =
    let (r, logs1) = Writer.run m
    match r with
    | Ok a ->
      let (b, logs2) = Writer.run (f a)
      Writer <| fun () -> b, logs1 @ logs2
    | Error e ->
      Writer <| fun () -> Error e, logs1
```

Notice the signature of this function is exactly the same as the for the singular wrapping cases above. The generic types `'a` and `'b` are now a bit more embellished. Even the description of the function still applies. There is just now a bit more going on in order to 'unwrap' the inner value.

When handling combined types, we have to 'unwrap' according to the order of the types. Our type here is `WriterResult`, meaning that `Writer` wraps `Result`. We therefore need to unwrap `Writer` before unwrapping `Result`.

So our first step is to unwrap the `Writer`, revealing any written logs and a `Result`. Now, we can unwrap the `Result` to get to our inner value. If this value is `Ok` we preserve the meaning of `Result` by continuing to apply our function `f` to the inner value. However, `f` is a function that returns `Writer`, so we must unwrap that too to get another value and some more logs. This second value the result type that we want to return, along with all the logs. We therefore wrap in up in a new `Writer` with the concatenated logs.

If the first `Result` we encountered by unwrapping our writer turned out to be the `Error` case then we return this `Error` wrapped in a `Writer` without using `f` at all.

### All Three

The same wrapping and unwrapping technique applies when adding a third outer type. We simply have to accommodate the correct order of the types. Again, the signature and description do not change as the meaning of `bind` is consistent no matter how many outer types there are.

```fsharp
module AsyncWriterResult =

  let bind f m = async {
    let! w1 = m
    let (r, logs1) = Writer.run w1
    match r with
    | Ok a ->
      let! w2 = f a
      let (b, logs2) = Writer.run w2
      return Writer <| fun () -> b, logs1 @ logs2
    | Error e ->
      return Writer <| fun () -> Error e, logs1 }
```

This looks very similar to the `WriterResult` type. The difference is obviously the reference to the `async` computation expression. This is needed to unwrap in the correct order. We start with unwrapping the `Async` type using `let!` to reveal a `WriterResult`. This then needs to be unwrapped to reveal a `Result` and some logs. If the `Result` is `Ok` we can apply our function and which gives us an `AsyncWriterResult`. We have to again unwrap with `let!` to get a `WriterResult`. We unwrap the `WriterResult` leaving us just a `Result` and some more logs. The `Result` is returned in a new `Writer` with the concatenated logs using the `return` statement to wrap in an `Async`. As before, if the inner `Result` is an `Error` case we do not want to apply `f` and exit early.

## Return

Before we can examine a use case for this we need to provide an implementation of `return`. This function is required before we can use a computation expression. Notice again how the order is important in wrapping the value.

```fsharp
module Result =

  let retn x =
    Ok x

module Async =

  let retn x = async {
    return x }

module Writer =

  let retn x =
    Writer <| fun () -> x, []

module AsyncWriterResult =

  let retn x =
    Result.retn x |> Writer.retn |> Async.retn

module Builder =

  type AsyncWriterResultBuilder () =
    member __.Return (x) = AsyncWriterResult.retn x
    member __.ReturnFrom (m: Async<Writer<'a, Result<'b, 'c>>>) = m
    member __.Bind (m, f) = AsyncWriterResult.bind f m

  let asyncWriterResult =
    new AsyncWriterResultBuilder ()
```

### Writing

Now we have our computation expression defined we need one more thing. A handy write function that lets us write logs when required. Notice again how we are wrapping in the corrrect order. The `write` function implicitly returns `unit` type, and will be used with the `do!` directive.

```fsharp
module AsyncWriterResult =

  let write log = async {
    return Writer (fun () -> Result.retn (), [log]) }
```

## In Practice

Lets define some bindable functions and compose them in a `AsyncWriterResult` computation expression.

```fsharp
let getThing x = async {
  do! Async.Sleep 100
  return Result.retn x |> Writer.retn  }

let checkThing r =
  if r > 0 then Ok "thing is ok" else Error "thing is bad"
  |> Writer.retn |> Async.retn

let expr x = asyncWriterResult {
  do! write "getting thing"
  let! thing = getThing x
  do! write "checking thing"
  let! result = checkThing thing
  do! write "returning thing"
  return result }

let eval x =
  expr 0
  |> Async.RunSynchronously
  |> Writer.run
```

Notice the difference in output for each call below. The logs accumulate only as far the `Result` remains `Ok`. This shows we have preserved the meaning of the `Result` type while successfully adding `Writer` and `Async` layers.

```
> eval 0;;
val it : Result<string,string> * string list =
  (Error "thing is bad", ["getting thing"; "checking thing"])

> eval 1;;
val it : Result<string,string> * string list =
  (Ok "thing is ok", ["getting thing"; "checking thing"; "returning thing"])
```


## Discussion

The `WriterResult` and `AsyncWriterResult` types implicitly return on the [error track](https://fsharpforfunandprofit.com/posts/recipe-part2/) when an `Error` case is encountered meaning any `do!` log will not be evaluated after any `let!` that returns an `Error`. As demonstrated above. But what if you _do_ want to log after receiving an `Error`?

Lets say we want to log the duration of a function call that returns a `AsyncWriterResult`. We can only know the elapsed time after the function has completed. However, if the function results in an `Error` we don't have the opportunity to stop the clock or write the log!

The `measure` function below can help us out. By unwrapping the outer `Async` type by invoking the function with a `unit` we have a `WriterResult`. We run this to get at our inner `Result` and any logs written. We don't care which case our `Result` is because we want to write the elapsed time in either case. We can just bundle up our `Result` with the concatenated logs in a new `Writer` and using `return!` because we have a fully formed `AsyncWriterResult` already at the end of the expression.

```fsharp
let measure name f = asyncWriterResult {
  return! async {
    let sw = Stopwatch.StartNew ()
    let! w = f ()
    let (r, logs) = Writer.run w
    let log = sprintf "%s: %i" name sw.ElapsedMilliseconds
    return Writer <| fun () -> r, log :: logs } }

let failToGetThing x = async {
  do! Async.Sleep 100
  return Error "could not get thing" |> Writer.retn  }

let expr x = asyncWriterResult {
  do! write "getting thing"
  let! thing = measure "elapsed" <| fun () -> failToGetThing x
  do! write "returning thing"
  return thing }
```

Now the output includes the `elapsed` log despite resulting in an `Error`.

```
> eval 1;;
val it : Result<string,string> * string list =
  (Error "could not get thing", ["getting thing"; "elapsed: 105"])
```