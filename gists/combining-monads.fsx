module Async =

  let retn x = async {
    return x }

  let bind f m = async {
    let! x = m
    return! f x }


module Result =

  let retn =
    Ok

  let bind f = function
    | Ok x -> f x
    | Error e -> Error e


type Writer<'w, 't> =
  Writer of (unit -> 't * 'w)

module Writer =

  let run<'w,'t> (Writer w) : ('t * 'w) =
    w ()

  let retn a =
    Writer <| fun () -> a, []

  let bind m f =
    let (a, w1) = run m
    let (b, w2) = run (f a)
    Writer <| fun () -> b, w1 @ w2


[<AutoOpen>]
module AsyncWriterResult =

  let retn x =
    x |> Result.retn |> Writer.retn |> Async.retn

  let bind f m = async {
    let! w = m
    let (r, w1) = Writer.run w
    match r with
    | Ok a ->
      let! ww = f a
      let (b, w2) = Writer.run ww
      return Writer <| fun () -> b, w1 @ w2
    | Error e ->
      return Writer <| fun () -> Error e, w1 }

  let write log = async {
    return Writer (fun () -> Result.retn (), [log]) }


type AsyncWriterResultBuilder () =
  member __.Return (x) = AsyncWriterResult.retn x
  member __.ReturnFrom (m: Async<Writer<'a, Result<'b, 'c>>>) = m
  member __.Bind (m, f) = AsyncWriterResult.bind f m
  member __.Zero () = __.Return ()

let asyncWriterResult =
  new AsyncWriterResultBuilder ()


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
  expr x
  |> Async.RunSynchronously
  |> Writer.run

eval 1

open System.Diagnostics

let failToGetThing x = async {
  do! Async.Sleep 100
  return Error "could not get thing" |> Writer.retn  }

let measure name f = asyncWriterResult {
  return! async {
    let sw = Stopwatch.StartNew ()
    let! w = f ()
    let (r, logs) = Writer.run w
    let log = sprintf "%s: %i" name sw.ElapsedMilliseconds
    return Writer <| fun () -> r, log :: logs } }

let exprWithMeasure x = asyncWriterResult {
  do! write "getting thing"
  let! thing = measure "elapsed" <| fun () -> failToGetThing x
  return thing }

let s : Result<string,string> * string list = 
  exprWithMeasure 0
  |> Async.RunSynchronously
  |> Writer.run 