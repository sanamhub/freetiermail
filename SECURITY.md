# Security policy

## Supported versions

Only the latest release on nuget.org receives fixes. While FreeTierMail is 0.x, that is the latest
prerelease.

## Reporting a vulnerability

Report privately through
[GitHub security advisories](https://github.com/sanamhub/freetiermail/security/advisories/new). Do not open a public issue.

Include the smallest code that shows it, the package versions and the .NET version. Expect a
first reply within a week. This is a one-person project, so a fix can take longer; the advisory
says when one is ready.

Security bugs here include: an API key, recipient address, subject or body reaching a log,
trace, metric or exception message; a request sent to a host other than the provider's; a
failover that sends a message a provider may already have accepted when the caller did not opt
in.

A provider changing its API is a normal bug. Open an issue.
