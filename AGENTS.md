# Working in this repository

Instructions for anyone, human or AI agent, who changes code or docs here. `CLAUDE.md` imports
this file.

## What this is

FreeTierMail, an MIT-licensed .NET 10 library that sends transactional email through several
providers' free tiers as one sender, routed by the quota each has left.

The design documents (`docs/PLAN.md`, `docs/IMPLEMENTATION.md`, `docs/adr`, `docs/research`) are
kept by the maintainer and are not in the public repository. If they are missing from your
checkout, ask the maintainer rather than guessing.

## Commands

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

```bash
dotnet pack -c Release -o artifacts/packages
```

A task is done only when all three pass with zero warnings. Warnings are errors. Do not add
`NoWarn` or `#pragma` to silence one without a one-line reason.

## Hard rules

1. **No real secrets or personal data anywhere**: code, tests, fixtures, logs, issues, commits.
   Test keys look like `test-key-0000000000000000`; test addresses use `example.com`.
2. **Never log, trace or put in an exception message** an API key, a recipient address, a
   subject or a body.
3. **Tests never call a live provider.** Recorded or documented responses only.
4. **Never send a message twice on the library's own initiative.** A request that may have been
   accepted is `Unknown`, not a reason to fail over.
5. **Public API changes** update `PublicAPI.Unshipped.txt` in the same commit.
6. Follow the writing rules in
   [.claude/skills/writing-style/SKILL.md](.claude/skills/writing-style/SKILL.md): no em dashes,
   numbers over adjectives, and **no AI attribution** (`Co-Authored-By`, `Generated with`) in
   commits, PRs or files.

## Commits

Conventional Commits: `type(scope): summary`, imperative, lower case, under 72 characters, body
says why.
