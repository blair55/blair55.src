---
title: "A functional wrapper around the .net AWS DynamoDB SDK"
date: 2019-11-29T10:00:00Z
draft: false
tags:
  - fsharp
  - functional
  - monads
  - aws
  - dynamodb
---

We're going to take a tour of some F# capabilities and
use them to enforce the constraints of the DynamoDB client.
We'll look at domain modeling with discriminated unions,
data access using the reader applicative,
and error handling with the result type.

## DynamoDB and Data Types

Before we get started, let's summarise DynamoDB and its [supported types](https://docs.aws.amazon.com/en_pv/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html).

- DynamoDB is a key-value & document database.
- DynamoDB tables are schemaless so each record can contain a different number of attributes.
- A record attribute has a string name and a value that is one of three types:
  Scalar, Set, and Document.

A **Scalar** is a single value of a particular primitive type:
string, number, boolean, binary or null.
Number and binary values require string conversion before being sent over the network.

A **Set** is a collection of distinct items of the same type.
The set types are string, number, and binary. A set cannot be empty.

A **Document** is made from lists and maps.
Lists are non-distinct collections of non-uniform type.
Maps are just like name-value pair json objects and naturally support nesting.
Maps and lists are rich objects because they are compositions of the scalar data types.
Below is an example record.

```json
{
  "Day": "Monday",
  "UnreadEmails": 42,
  "ItemsOnMyDesk": [
    "Coffee Cup",
    "Telephone",
    {
      "Pens": { "Quantity": 3 },
      "Pencils": { "Quantity": 2 },
      "Erasers": { "Quantity": 1 }
    }
  ]
}
```

## The Write Model

This description of the data types is enough to start building a Write Model.
**We're going to build a layer between our code and the AWS SDK**,
so let's review the exposed API.

```bash
$ paket add AWSSDK.DynamoDBv2
```

The `AmazonDynamoDBClient` type exposes a `PutItemAsync` method for
writing records to a table.
This method takes two parameters, the target table name as a `string` and a `Dictionary<string, AttributeValue>` of attributes to write.
The dictionary is what we're interested in building with our model.
Here's an example of directly interfacing with the library to write an item.

```fsharp
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model
open System.Collections.Generic

use client = new AmazonDynamoDBClient ()

let attributes =
  [ "name" , new AttributeValue (S = "foo")
    "total", new AttributeValue (N = "123.45")
    "raw"  , new AttributeValue (B = "encoded binary value") ]
  |> dict
  |> Dictionary<string,AttributeValue>

let response =
  new PutItemRequest ("my_table_name", attributes)
  |> client.PutItemAsync
```

The `AttributeValue` type has a corresponding constructor argument for each type.
The arguments are optional, so it is possible to provide
zero arguments or more than one argument when constructing.
We can improve upon this in our model.
Notice that we provide Number and Boolean values as strings.
We'll perform the conversion when we map our model to Attributes at runtime.

### Building Declaratively

We don't want to use the `AttributeValue` library type directly because it's cumbersome and error-prone.
We also want to declare our attributes with minimum syntactical ceremony - the F# way!

To achieve this, we can model the documented type cases with discriminated unions.
Notice that the `DocList` case is a recursive type because it references itself,
while the `DocMap` case is mutually recursive because it references the `Attr` type.

```fsharp
type Attr =
  | Attr of name:string * AttrValue
and  AttrValue =
  | ScalarString of string
  | ScalarDecimal of decimal
  | ScalarBinary of string
  | ScalarBool of bool
  | ScalarNull
  | SetString of string Set
  | SetDecimal of decimal Set
  | SetBinary of string Set
  | DocList of AttrValue list
  | DocMap of Attr list
```

We can then build our example document with a collection of `Attr`s.

```fsharp
let attributes =
  [ Attr ("Day", ScalarString "Monday")
    Attr ("UnreadEmails", ScalarDecimal 42m)
    Attr ("ItemsOnMyDesk",
      DocList
        [ ScalarString "Coffee Cup"
          ScalarString "Telephone"
          DocMap
            [ Attr ("Pens", DocMap [ Attr ("Quantity", ScalarDecimal 3m) ])
              Attr ("Pencils", DocMap [ Attr ("Quantity", ScalarDecimal 2m) ])
              Attr ("Erasers", DocMap [ Attr ("Quantity", ScalarDecimal 1m) ])
            ]
        ])
  ]
```

Now we need a way to map our `Attr list` into the target type:
`Dictionary<string,AttributeValue>`.
Notice again the `rec` & `and` directives are required as the functions are
mutually recursive, reflecting the data structure.

```fsharp
open System.IO
open System.IO.Compression

let toGzipMemoryStream (s:string) =
  let output = new MemoryStream ()
  use zipStream = new GZipStream (output, CompressionMode.Compress, true)
  use writer = new StreamWriter (zipStream)
  writer.Write s
  output

let rec mapAttrValue = function
  | ScalarString s  -> new AttributeValue (S = s)
  | ScalarDecimal n -> new AttributeValue (N = string n)
  | ScalarBinary s  -> new AttributeValue (B = toGzipMemoryStream s)
  | ScalarBool b    -> new AttributeValue (BOOL = b)
  | ScalarNull      -> new AttributeValue (NULL = true)
  | SetString ss    -> new AttributeValue (SS = ResizeArray ss)
  | SetDecimal ns   -> new AttributeValue (NS = ResizeArray (Seq.map string ns))
  | SetBinary bs    -> new AttributeValue (BS = ResizeArray (Seq.map toGzipMemoryStream bs))
  | DocList l       -> new AttributeValue (L = ResizeArray (List.map mapAttrValue l))
  | DocMap m        -> new AttributeValue (M = mapAttrsToDictionary m)

and mapAttr (Attr (name, value)) =
  name, mapAttrValue value

and mapAttrsToDictionary =
  List.map mapAttr >> dict >> Dictionary<string,AttributeValue>
```

The function below uses our map function before
calling the `PutItemAsync` library method.
It then converts the TPL `Task<'T>` type into an F# `async`
and models the response with the `Result` type.

```fsharp
open System.Net

let putItem tableName fields : string -> Attr list -> Result<Unit, string> =
  use client = new AmazonDynamoDBClient()
  new PutItemRequest (tableName, mapAttrsToDictionary fields)
  |> client.PutItemAsync
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> fun r ->
    match r.HttpStatusCode with
    | HttpStatusCode.OK -> Ok ()
    | _ as status -> Error <| sprintf "Unexpected status code '%A'" status
```

This completes the first half of our journey. Now we can move onto reading an item.

## The Read Model

DynamoDB provides multiple ways of retrieving data.
We can use **Get** to retrieve a single item,
and **Query**, and **Scan** to return an item collection.

```fsharp
let attributes =
  [ "id" , new AttributeValue (S = "123") ]
  |> dict
  |> Dictionary<string,AttributeValue>

let response : GetItemResponse =
  new GetItemRequest ("my_table_name", attributes)
  |> client.GetItemAsync
  |> Async.AwaitTask
  |> Async.RunSynchronously

let item : Dictionary<string,AttributeValue> =
  response.Item
```

### Getting an item

The DynamoDB library expects a `Dictionary<string,AttributeValue>`
to specify the identifier of the single item we wish to retrieve.
This means we can use the write model to provide the identifier.
The item is also returned using the same `Dictionary<string,AttributeValue>`.
Our challenge, therefore, is to **turn the Dictionary into our domain type**.
This sounds like a good fit for the Reader applicative.

### Reader Applicative

Let's define a simple domain type that
we want to construct from the `GetItemResponse`
Having a function with explicit arguments
will make it easier to build the record
one property at a time.

```fsharp
type Order =
  { Name : string
    Description : string
    IsVerified : bool
    Quantity : int
    Cost : float }

let buildOrder name desc isVerified qty cost =
  { Name = name
    Description = desc
    IsVerified = isVerified
    Quantity = qty
    Cost = cost }
```

We're going to combine the work from two articles
to create our **Reader Applicative** below:
Matthew Podwysocki's
article on the [Reader Monad](http://webcache.googleusercontent.com/search?q=cache:EibmCdF8430J:codebetter.com/matthewpodwysocki/2010/01/07/much-ado-about-monads-reader-edition/+&cd=1&hl=en&ct=clnk&gl=uk) (cached)
and Scott Wlaschin's article that covers
[applicatives](https://fsharpforfunandprofit.com/posts/monadster-3/).

```fsharp
type Reader<'a, 'b> =
  Reader of ('a -> 'b)

module Reader =

  let run (Reader f) a =
    f a

  let retn a =
    Reader (fun _ -> a)

  let map f r =
    Reader (fun a -> run r a |> f)

  let apply f r = // Reader applicative function
    Reader (fun a -> run r a |> run f a)
```

The `Reader` type is completely generic.
It's a single-case union type that contains a simple function: `a -> b`.
The `Reader` module contains our helper functions.
The `run` function is needed to unwrap the reader function,
evaluate it with the provided input `a` and return the `b`.
`run` is the only function in the module that doesn't return a `Reader`.

`retn` lets us create a `Reader` from a single value.
It embeds the value inside a function in which the single argument is ignored.

`map` lets us run a function against the returned value of a reader function.
This is useful when the input value to our function is wrapped inside a `Reader`.

`apply` takes two `Reader` arguments and extracts the values from both by
calling the `run` function.
The two extracted values are related.
The first value is a function. The second value is the
first argument to the first extracted function.
We 'apply' the function to the value
and wrap the returned value in a `Reader`
This is useful when your function is wrapped inside a `Reader`,
and arguments to the function are also wrapped in `Reader`.

If we apply our `map` function to our `buildOrder` function
this is exactly what we get: a multi-argument function wrapped in a reader.
With some helper functions to extract the correct value from the library
`Attribute` and some custom operators we can create our record with minimal syntax.

```fsharp
let getItem tableName reader fields =
  new GetItemRequest (tableName, mapAttrsToDictionary fields)
  |> client.GetItemAsync
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> fun r -> r.Item
  |> Reader.run reader

let (<!>) = Reader.map
let (<*>) = Reader.apply

let extract f (d:Dictionary<string,AttributeValue>) = f d
let readString key   = extract (fun d -> d.[key].S) |> Reader
let readBool   key   = extract (fun d -> d.[key].BOOL) |> Reader
let readNumber key f = extract (fun d -> d.[key].N) |> f |> Reader

let readOrder =
  buildOrder
  <!> readString "name"
  <*> readString "description"
  <*> readBool   "isVerified"
  <*> readNumber "quantity" int
  <*> readNumber "cost" float

let getOrder id : Order =
  getItem "orders" readOrder [ Attr ("id", ScalarString id) ]
```

### Nested Objects

Let's extend our example to contain a nested document.
We can see that the reader pattern lets us define functions
that will plug into a parent reader expression.

```fsharp
type Merchant =
  { Id : int
    Region : string }

let buildMerchant id region =
  { Id = id
    Region = region }

let readMerchant =
  buildMerchant
  <!> readNumber "id" int
  <*> readString "region"

type Order =
  { Name : string
    Description : string
    IsVerified : bool
    Merchant : Merchant // new field
    Quantity : int
    Cost : float }

let buildOrder name desc isVerified merchant qty cost =
  { Name = name
    Description = description
    IsVerified = isVerified
    Merchant = merchant
    Quantity = qty
    Cost = cost }

let readOrder =
  buildOrder
  <!> readString   "name"
  <*> readString   "description"
  <*> readBool     "isVerified"
  <*> readMerchant "merchant"
  <*> readNumber   "quantity" int
  <*> readNumber   "cost" float
```

### Result Reader

In our example, we are reading items from the `Dictionary` unsafely.
Let's introduce the `Result` type to help us handle errors more gracefully
and provide better for support optional fields on our domain objects.

```fsharp
module ReaderResult =

  let retn a =
    Result.Ok a |> Reader.retn

  let map f =
    Result.map f |> Reader.map

  let apply f r =
    Reader <| fun a ->
      let fa = Reader.run f a
      let fb = Reader.run r a
      match fa, fb with
      | Ok a, Ok b -> Ok (a b)
      | Error e, _ -> Error e
      | _, Error e -> Error e
```

There is no type definition required as
the `map` and `apply` functions return a
composed type: `Reader<'a,Result<'b,'c>>`.

Notice that the `apply` function
has to pattern patch against two `Result`s.
The function inside the first result is
applied to the value inside the second result
provided that both `Result`s are `Ok`.

The helper functions below read from the
`GetItemResponse` safely by turning the
contained dictionary into a `Map<string,Attribute>`
and using the `TryFind` function.
This means we will return an `Error` case if we
attempt to read a key from the dictionary that
doesn't exist.

```fsharp
let getItem tableName reader fields =
  new GetItemRequest (tableName, mapAttrsToDictionary fields)
  |> client.GetItemAsync
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> fun r -> r.Item
  |> Seq.map (|KeyValue|)
  |> Map.ofSeq
  |> Reader.run reader
  |> Ok

let mapFind key =
  Map.tryFind key
  >> function
  | Some x -> Ok x
  | None -> Error <| sprintf "could not find key %s" key

let readString key =
  Reader (mapFind key >> Result.map (fun (a:AttributeValue) -> a.S))

let readStringAs key f =
  Reader (mapFind key >> Result.map (fun (a:AttributeValue) -> a.S) >> Result.bind f)

let readStringAsOption key f =
  Reader (Map.tryFind key >> Option.map (fun (a:AttributeValue) -> a.S) >> Option.bind f)

let readStringSet key =
  Reader (mapFind key >> Result.map (fun (a:AttributeValue) -> Set.ofSeq a.SS))

let (<!>) = ResultReader.map
let (<*>) = ResultReader.apply

let stringToGuid e (date:String) =
  date
  |> Guid.TryParse
  |> function | true, x -> Ok x | _ -> Error "could not parse to guid"

let stringToDate (date:String) =
  date
  |> DateTime.TryParse
  |> function | true, x -> Some x | _ -> None

type Order =
  { Name : string
    OrderId : Guid
    Fulfilled : DateTime option
    Tags : string Set }

let buildOrder name orderId fulfilled tags =
  { Name = name
    OrderId = orderId
    Fulfilled = fulfilled
    Tags = tags }

let readOrder =
  buildOrder
  <!> readString "name"
  <*> readStringAs "orderId" stringToGuid
  <*> readStringAsOption "fulfilled" stringToDate
  <*> readStringAsSet "tags"

let getOrder id : Result<Order,string> =
  getItem "orders" readOrder [ Attr ("id", ScalarString id) ]
```

## That's All!

We've only just scratched the surface of the .net library for DynamoDB.
There are plenty more features to model functionally,
including paging, updating and filter expressions.
If you need a feature-complete library then check out
[FSharp.AWS.DynamoDB](https://github.com/fsprojects/FSharp.AWS.DynamoDB).

This article mainly intends to show an example of domain modeling
and the reader applicative, but the green shoots of a new library are visible!

Happy F# Advent Calendar 2019!
