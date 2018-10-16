---
title: "Scripting with AWS Parameter Store" 
date: 2018-10-12T16:35:01+01:00
draft: false
tags:
    - aws
    - parameter-store
    - bash
    - fsharp
    - jq
---

Ever need to write a short script as part of development to get feedback on a portion of code? Ever need to add private authentication values to that script? Ever wanted to add the script to source control but couldn't because it contained your private authentication?

> Enter: AWS Parameter Store

This AWS service acts as a key-value store. Add your private settings such as usernames, passwords, connection strings etc and they can be retrieved programatically by their key name. This lets you commit your handy script to source control by keeping it free from private values.

## Example

> Required: Your own AWS Account and the [AWS CLI](https://docs.aws.amazon.com/cli/latest/reference/ssm) installed and configured. Make sure the aws output type configuration is json. 
> Required: [jq](https://stedolan.github.io/jq/manual/) cli

Add a parameter to the store using the AWS CLI
```bash
$ aws ssm put-parameter --name "authtoken" --type "String" --value "foobar"
```

Query the parameter store with the key name
```bash
$ aws ssm get-parameter --name "authtoken"
> {
    "Parameter": {
        "Version": 1,
        "Type": "String",
        "Name": "authtoken",
        "Value": "foobar"
    }
}
```

Notice the output is in json format. To isolate the parameter value we can pipe the response into a jq expression:
```bash
$ aws ssm put-parameter --name "authtoken" --type "String" --value "foobar" | jq -r .Parameter.Value
> foobar
```

Armed with this command we can create a script that is absent of hard-coded private values.

```bash
get_param() {
    P=$(aws ssm get-parameter --name "$1" | jq -r '.Parameter.Value')
    echo "$P"
}

AUTH_TOKEN=$(get_param "authtoken")
echo AUTH_TOKEN
```

## Taking it Further

Ever needed to write a script using a general purpose language?

In a recent example, I needed to execute a script written in F#. The script required private values from the parameter store.

I simply extended the original script to populate environment variables followed by a call to invoke the F# script.

```bash
get_param() {
    P=$(aws ssm get-parameter --name "$1" | jq -r '.Parameter.Value')
    echo "$P"
}

AUTH_TOKEN=$(get_param "authtoken") \
BASE_URL=$(get_param "baseurl") \
fsharpi script.fsx
```

```fsharp
let env =
    System.Environment.GetEnvironmentVariable

let log =
    printfn "%s"

env "AUTH_TOKEN" |> log
env "BASE_URL" |> log
```

## Path Support

Ever needed to get a collection of parameters?

AWS Parameter Store key names can be provided as a path. This allows you to group parameters logically, for example by system or environment, or both.

```bash
$ aws ssm put-parameter --name "/production/authtoken" --type "String" --value "foobar"
$ aws ssm put-parameter --name "/production/baseurl" --type "String" --value "www.example.url"
```

Parameters can then be fetched by path. Meaning your production system can retrieve all the keys under the production path.

```bash
$ aws ssm get-parameters-by-path --path "/production"
> {
    "Parameters": [
        {
            "Version": 1,
            "Type": "String",
            "Name": "/production/authtoken",
            "Value": "foobar"
        },
        {
            "Version": 1,
            "Type": "String",
            "Name": "/production/baseurl",
            "Value": "www.example.url"
        }
    ]
}
```

Note the output is a collection of parameters.