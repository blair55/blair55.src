---
title: "Integration testing a locally hosted serverless project"
date: 2020-05-10T11:04:23+01:00
draft: false
tags:
  - aws
  - serverless
  - npm
  - npm-scripts
  - integration-testing
  - mocking
---

## Strategy

There are many choices for running integration/functional/e2e tests with a serverless project.
There are two main modes of operation for integration testing.
The first is cloud-hosted infrastructure in which a temporary copy of the stack is deployed
and the intregration tests are pointed at the resources.
This has the benefit of increasing the scope of the integration tests to include
permissions and configuration.
The door is also open for security or load testing, as the target environment is almost
identical to what production will be.
The downside of testing against cloud hosted infrastructure is that you introduce a network
dependency into your stack. Your internet connection must be reliable, and your cloud
host must be operational for your tests to succeed.
Furthermore, the overhead in creating and tearing down stacks can be wearisom in an otherwise
fast-paced development feedback loop.

This article will focus on the second mode of operation:
integration testing against a locally hosted stack.
A local stack tends to be faster to setup and work with.
It has no network dependency at runtime so reduces the scope of the debugging exercise.
Running integration tests against a local stack is also slightly easier to repeat in
a CI environment, without the requirement of sharing cloud credentials.
Lastly, local environments can never be shared with other developers.
The temptation for cloud hosted infrastructure is to spin up a semi-permanent stack
that is shared by a team. This can cause problems when two developers are working on project
at the same time. Extra effort is required to enforce a one-cloud-stack-per-developer policy.

Its important to note that these options are not mutually exclusive.
There is value in exercising tests against both a locally-hosted and cloud-hosted
stack if you feel the extra effort is worth it. For example, using a local stack during
development whilst only engaging cloud resources during the CI pipeline would make for a
reasonable blend of strategies.

## Serverless plugins

The Serverles Framework ecosystem of plugins provides a starting point for spinning up
local infrastructure. A suite of 'offline' plugins exist, each one targeted at a specific
aws service. A plugin, once installed, will start the aws service on a local port, and
integrate with your serverless file in order to reproduce the specifc resources.

For example, the `serverless-dynamodb-local` plugin will start dynamodb on port 8000.
It will also inspect your serverless file `Resources` section and create any tables
as specified. Enforcing the parameterised table name, attributes and key schema.
Plugins for most popular aws services [exist](https://github.com/topics/serverless-offline),
including S3, SQS, SNS & Kinesis.

## Dude, where's my docker-compose?

Another solution for spinning up local infrastructure is `docker-compose`.
I'm a big fan of this product and have used it extensively in other solutions.
Docker-compose has the benefit of not requiring any further dependencies in
order to run the desired services. For example, a requirement to run the
`serverless-dynamodb-local` plugin is that the Java-JRE must be installed
not only locally but also in your CI environment. Different plugins may have
different dependencies, but with docker-compose the off-the-shelf image for any
tool is completely self-contained.

However, I think the major benefit of using serverless plugins is the shared
definition of specific resources. With docker-compose, I would need to express
the definition of a dynanmoDB table again in a different format. This is
duplication of code and an added maintenance burden.

Furthermore, docker-compose is a something of sledge-hammer. Whilst researching
the following approach I wanted to see if I could reproduce a docker-compose-esque
workflow just using npm packages. Maintaining a single, centralized configuration
file in `package.json` and reducing the mental overhead. The aim is to provide
the developer with a single command that will kick off dependencies before running
the tests.

## npm packages

Beyond the necessary serverless plugins specific to your project,
here are 5 npm packages useful in reproducing a frictionless integration testing
workflow. These packages work together to orchestrate a test run and as such are
co-dependent.

### [wait-on](https://www.npmjs.com/package/wait-on)

Blocks until a network dependency is satisfied. Useful for wating
for a resource to start up before continuing. In our case, we use it after we have
kicked-off a local aws service via a serverless plugin. We give the command the
local address, when the command returns we know the service has started
successfully and is available.

### [pm2](https://www.npmjs.com/package/pm2)

Process manager used to deamonize locally hosted services.
To reduce friction in our workflow, we don't want to have to juggle multiple
console windows. We're aiming to start the local stack in the background so that
we can continue to run our integration tests in the same console window.

### [npm-run-all](https://www.npmjs.com/package/npm-run-all)

Provides more flexibility in npm script execution. Allows for running multiple
npm script commands in sequence, in parallel or both together. Supports
glob pattern matching for script names, which reduces maintenance as we add more
local service depedencies. Given multiple local services that we
wish to start up, it would be useful to start them in parallel.

### [filename-basepath](https://www.npmjs.com/package/@totallymoney/filename-basepath)

Provides a low-ceremony approach to fulfilling http dependencies. Your project
may not only depend on aws services but also other http endpoints,
perhaps implemented by a third-party. There are several options for hosting mock endpoints,
notably [json-server](https://www.npmjs.com/package/json-server), but this project,
authored by myself ;-) provides what I think is the right level of control for the
minimum overhead. `express.js` routes are provided with custom files. Multiple endpoints
are supported as each route is hosted on a unique basepath.

### [env-cmd](https://www.npmjs.com/package/env-cmd)

Starts a process with environment variables provided by a `.env` file. Users of
docker-compose will be familiar with this feature that injects settings from a sibling `.env`
file into the hosted services. This package replicates that behaviour and is necessary
for the target process under test to know where its depedencies are located. For example,
we need to parameterise the dynamoDB client such that it tries to reach `http://localhost:8000`
and not the default aws service url. The same follows for other aws services or mocked http
endpoints.

## Pulling it together

- `serverless.yml`

```yml
plugins:
  - serverless-dynamodb-local
  - serverless-s3-local

custom:
  s3:
    port: 7000
    directory: s3
  dynamodb:
    start:
      port: 8000
      migrate: true
      inMemory: true
    stages: local

resources:
  Resources:
    DynamoDBTable:
      Type: AWS::DynamoDB::Table
      Properties:
        TableName: mytable-${self:provider.stage}
        AttributeDefinitions:
          - AttributeName: PK
            AttributeType: S
        KeySchema:
          - AttributeName: PK
            KeyType: HASH
```

- `.env`

```bash
TABLE=mytable-local
S3_URL=http://localhost:7000
DYNAMODB_URL=http://localhost:8000
HTTP_BASE_URL=http://localhost:3000/my-endpoint
```

- `package.json`

```json
{
  "scripts": {
    "stop": "pm2 delete -s all || true",
    "start:mocks": "pm2 start -s filename-basepath -- mocks && wait-on tcp:3000",
    "start:db": "pm2 start -s --name db sls -- dynamodb start && wait-on tcp:8000",
    "start:s3": "pm2 start -s --name s3 sls -- s3 start && wait-on tcp:7000",
    "pretest": "npm-run-all stop --parallel start:*",
    "test": "env-cmd dotnet run -p tests"
  },
  "devDependencies": {
    "@totallymoney/filename-basepath": "^0.0.4",
    "env-cmd": "^10.1.0",
    "npm-run-all": "^4.1.5",
    "pm2": "^4.4.0",
    "serverless": "^1.70.0",
    "serverless-dynamodb-local": "^0.2.39",
    "serverless-s3-local": "^0.5.4",
    "wait-on": "^4.0.1"
  }
}
```

Notice the usage of the `pretest` script. This command is implicitly triggered when `test` is called.
Therefore, the following happens when `npm run test` is executed:

1. Stop any existing services `pm2` may have already started,
   ignoring the error returned if `pm2` has no running services.
2. Start the local stack in parallel and wait for each service to be reachable.

   - http endpoints
   - dynamodb
   - s3

3. Run the test suite in the context of the environment variables provided by the `.env` file.

## Enjoy!

This arrangement is providing value is a number of serverless projects. It has been good reason
to explore the npm ecosytem and successfully reproduces `docker-compose` like behaviour.
