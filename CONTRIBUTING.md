# Contributing

FreeTierMail is a .NET library that sends transactional email through several providers as one
sender. Bug reports, fixes and documentation improvements are welcome. For a new feature or an API change, open an issue first so the design can be agreed before you write code.

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems as
described in [SECURITY.md](SECURITY.md), not in a public issue.

## Setup

You need the .NET SDK version in [`global.json`](global.json). Nothing else.

```bash
dotnet build -c Release
dotnet test -c Release
```

Tests never call a live provider. They use recorded fixtures and a fake clock.

## Pull requests

- Keep a pull request to one change. Small is easier to review.
- Add or update tests for any behaviour change.
- Public API changes go in `src/<project>/PublicAPI.Unshipped.txt`. The build fails if you forget.
- Add a line to the `Unreleased` section of [`CHANGELOG.md`](CHANGELOG.md) for anything a user of
  the package would notice.
- A significant design decision gets an ADR. The ADRs are kept by the maintainer outside this
  repository, so raise the decision in an issue first. ADRs are not edited after they are
  accepted; a later ADR supersedes an earlier one.
- CI must pass on Linux and Windows, including the Native AOT consumption check.
- If you used an AI tool, you are still the author: read every line, and be ready to explain it
  in review.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/): `type(scope): summary`, in the
imperative, lower case, no trailing period. Types: `feat`, `fix`, `docs`, `chore`, `refactor`,
`test`, `build`, `ci`, `perf`. The body says why, not what. No AI attribution trailers.

## Code style

The rules in [`.editorconfig`](.editorconfig) are enforced by the build. Public members need XML
documentation that says what the member does, what it returns and what breaks it. The full
writing rules are in [`.claude/skills/writing-style/SKILL.md`](.claude/skills/writing-style/SKILL.md).
