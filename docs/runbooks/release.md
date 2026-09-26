# Runbook: release

For the maintainer. SemVer, changelog, green CI, approval, rollback plan, post-release check.

## Before tagging

1. `main` is green. `PublicAPI.Unshipped.txt` is empty in every project (move entries to
   `Shipped`).
2. `CHANGELOG.md` has a section for the new version; `<Version>` in `Directory.Build.props`
   matches.
3. Run the live checklist below with your test accounts, each sending to a mailbox you own. Keys
   come from your environment; never paste them into a terminal that records history to a
   shared place.

| Provider | Live check |
| --- | --- |
| Brevo | one text-only message arrives with the HTML copy; an unverified sender gives `ProviderFault`, not `RecipientRejected` (confirms the sender rule, a lead until checked) |
| Resend | a repeat with the same idempotency key sends once; the result carries the Resend id |
| Mailjet | one message arrives; note what a used-up free quota answers, and update the mapping if it is not 429 |
| Mailgun | one message arrives through the domain; a wrong domain gives `ProviderFault` |
| SMTP2GO | one message arrives; `succeeded` is 1 in the response |
| MailerSend | one message arrives; note the 429 body when the daily request quota runs out, and confirm the `quota` wording rule |
| Elastic Email | one message arrives with both parts |
| SMTP | one message through a relay on port 587 and one on 465 |
| Failover | with two providers, a revoked key on the first moves the send to the second |

## One-time setup

1. Settings > Environments: create `production` with yourself as required reviewer.
2. nuget.org > Trusted Publishing: add a policy for `sanamhub/freetiermail`, workflow
   `release.yml`, environment `production`.
3. Settings > Secrets and variables > Actions: add the secret `NUGET_USER` (the nuget.org account
   name the policy belongs to; it is not a key).
4. Run `release` from the Actions tab with `dry-run` ticked. It builds, verifies and uploads
   `release-files` without publishing. The SBOM step's `-fn` flag is unchecked until this run:
   compare it with `dotnet CycloneDX --help` in the log.

## Tag and publish

1. `git tag v<version>` and push the tag.
2. The release workflow runs preflight, then verify. Open the `release-files` artifact and check
   each `.nupkg` dependency group: nothing beyond the packages in `Directory.Packages.props`.
3. Approve the `production` deployment. The core is pushed first, then the rest.
4. `verify-published` then downloads every package from nuget.org and consumes it, plain and
   under Native AOT. It waits up to 45 minutes for indexing. If it fails on a timeout alone, run
   it again from the Actions tab with the tag.

## After publishing

- `verify-published` is green. Then install the new version into a scratch console from
  nuget.org and send one message.
- If a package is broken: unlist that version on nuget.org (never delete), fix forward with a
  patch release. Consumers pinned to the broken version keep working; new restores skip it.
