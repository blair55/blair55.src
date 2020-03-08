---
title: "Serverless package done right"
date: 2019-04-01T11:36:05+01:00
draft: false
tags:
  - aws
  - parameter-store
  - serverless
  - bash
---

## Problem

The Severless Framework [package command](https://serverless.com/framework/docs/providers/aws/guide/packaging/) claims to be useful in CI/CD workflows. The command results in the production of AWS Cloud Formation stack json files (or alternative cloud provider equivalent) on disk. These files can be bundled and considered the 'deployment artifact' at the end of the pipeline. The artifact can be provided to the `serverless deploy` command that could be run at a later date.

This two-step package/deploy process is very familiar, so I was lured into using the `serverless package` command. However, there is a major flaw in the design!

The behaviour of the command is to 'bake in' the value of the expected environment variables or [AWS Parameter Store](https://aws.amazon.com/systems-manager/features/#Parameter_Store) keys. This means your artifacts are not environment-agnostic. For example, if you include the stage parameter in the name of any functions or resources then the value of the stage parameter will be hardcoded in the artifacts. You therefore cannot promote these artifacts to any stage other than the one named when `serverless package` was called.

## Environment Agnostic

Build artifacts are supposed to be environment-agnostic. You should be able to deploy your artifacts to any environment and 'promote' through to production if desired. Artifacts must therefore be parameterised such that the target environment can provide everything the application needs. This is done using [environment variables](https://12factor.net/config). The only 'static' value in the artifact should be the version number, provided at build time.

One option to side-step this problem is to perform the package step once per potential target environment. A deployment would require the versioned artifacts for the target environment. This option is cumbersome during the build phase and complicates the deploy phase. However, the main problem is the potential for the baked-in environment variables to become stale.

Let's say we change the value of an environment variable after packaging for the production environment. This would require all the existing production-targeted artifacts to be rebuilt so as to bake-in the latest value. Only then are the new packages fit to be deployed. This creates CI overhead and requires consideration of how far back through artifact version history is sensible to rebuild.

## Just-in-time Packaging

The only realistic option is to postpone the package step, i.e. don't call `serverless package` explicitly. The package command is implicitly called within the deploy command (as long as the `--package` parameter is not provided). This package command deference is what happens when you call `serverless deploy` against your serverless.yml. The environment variables are baked-in 'just-in-time' so the artifacts are fit for the target environment.

We need to bring just-in-time packaging to CI to produce environment-agnostic build artifacts. The solution is to neglect the `serverless package` command and include the serverless.yml file into the build artifact. The deployment tooling must then execute the `serverless deploy` command against the serverless.yml from within the artifact sometime later.

To make the bundle completely self-contained the `package.json` file is also required alongside the serverless.yml. You could include your node_modules folder, or simply your `package-lock.json` or `yarn.lock` file and include a call to restore packages before `serverless deploy` is called, as shown below:

Build artifact contents

```bash
$ tree ./deploy
.
├── package.json
├── package.zip # code bundle
├── serverless.yml
└── yarn.lock
```

Serverless.yml package snippet

```yml
package:
  artifact: package.zip
```

Deployment steps

```bash
$ cd ./deploy
$ yarn install
$ yarn run serverless deploy --stage prod
```
