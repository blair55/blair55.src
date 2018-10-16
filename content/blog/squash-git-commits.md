---
title: "Squash Git Commits"
date: 2018-10-16T10:36:05+01:00
draft: false
tags:
    - git
    - zsh
---

Ever made several commits on a feature branch and needed to rebase? Rebasing multiple commits can be tedious if there is a conflict from an early commit, requiring a commit resolution in every commit that follows. It can be practical, therefore, to squash multiple commits into a single commit before rebasing.

Ever wanted to clean up your commit history before pushing? I often make quick commits if I'm required to switch branches, or spiking an idea during development that ends up being permanent. I'm left with messy commit messages that rarely follow [best practice](https://chris.beams.io/posts/git-commit/). Squashing commits to the rescue again! I can then take the time to carefully construct a commit message.

I will be using the oh-my-zsh aliases in this example below

> Required: Z-Shell & [oh-my-zsh](https://ohmyz.sh/)

```
$ glo
5fe9acf97 done
5306d9b9f Vevert 'try out a fix'
da1341005 more wip
ef8bb3f49 try out a fix
471bd09c1 wip
895143a59 start the work
2b2a51e70 (HEAD -> master, origin/master, origin/HEAD) Refactor layout
```

As you can see, the commit history is a mess. Let's squash all the commits since the _head_ of local master into one commit.

First, reset your work to the commit before your work started, identified by the hash.

```
$ git reset 2b2a51e70
```

Notice your changes are still on disk but git only sees them as unstaged changes - your ugly commits have disappeared.

```
$ gst
```

Now we can commit with a more thoughtful commit message.

```
$ gaa
$ gcam "Add feature x..."
```

Now we're in a position to rebase as the impact of a potential conflict has been reduced. Update your local copy of all remote branches and rebase on master.

```
$ gfo
$ grb origin/master
```

Resolve any conflicts using your editor then continue the rebase.

```
$ gaa
$ grbc
```

Confirm the history is as you expect, then push!

```bash
$ gst
$ gp -f # or use gpsup if the branch has not been pushed before
```

> For a list of aliases, simply use the command: `alias`